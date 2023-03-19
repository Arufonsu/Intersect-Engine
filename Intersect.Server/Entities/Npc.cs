using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Intersect.Enums;
using Intersect.GameObjects;
using Intersect.Logging;
using Intersect.Network.Packets.Server;
using Intersect.Server.Database;
using Intersect.Server.Database.PlayerData.Players;
using Intersect.Server.Entities.Combat;
using Intersect.Server.Entities.Events;
using Intersect.Server.Entities.Pathfinding;
using Intersect.Server.Maps;
using Intersect.Server.Networking;
using Intersect.Utilities;
using Stat = Intersect.Enums.Stat;

namespace Intersect.Server.Entities
{

    public partial class Npc : Entity
    {

        // Spell casting.
        private long _castFreq;

        /// <summary>
        /// Damage Map - Keep track of who is doing the most damage to this npc and focus accordingly
        /// </summary>
        public ConcurrentDictionary<Entity, long> DamageMap = new ConcurrentDictionary<Entity, long>();

        public ConcurrentDictionary<Guid, bool> LootMap = new ConcurrentDictionary<Guid, bool>();

        public Guid[] LootMapCache = Array.Empty<Guid>();

        public readonly bool Despawnable;

        // Moving.
        private long _lastRandomMove;

        // Pathfinding and targeting.
        private const int FindTargetDelay = 500;

        private const int TargetFailMax = 10;

        private const int ResetMax = 100;

        private readonly Pathfinder _pathFinder;

        private readonly byte _range;

        private readonly byte _moveRange;

        private Task _pathfindingTask;

        private Guid _moveTargetMap;

        private Point _moveTarget;

        private long _findTargetWaitTime;

        private int _targetFailCounter;

        private int _resetDistance;

        private int _resetCounter;

        private bool _resetting;

        /// <summary>
        /// The map on which this NPC was "aggro'd" and started chasing a target.
        /// </summary>
        private MapController _aggroCenterMap;

        /// <summary>
        /// The X value on which this NPC was "aggro'd" and started chasing a target.
        /// </summary>
        private int _aggroCenterX;

        /// <summary>
        /// The Y value on which this NPC was "aggro'd" and started chasing a target.
        /// </summary>
        private int _aggroCenterY;

        /// <summary>
        /// The Z value on which this NPC was "aggro'd" and started chasing a target.
        /// </summary>
        private int _aggroCenterZ;


        public Npc(NpcBase myBase, bool despawnable = false) : base()
        {
            Name = myBase.Name;
            Sprite = myBase.Sprite;
            Color = myBase.Color;
            Level = myBase.Level;
            Immunities = myBase.Immunities;
            Base = myBase;
            Despawnable = despawnable;

            for (var i = 0; i < (int)Enums.Stat.StatCount; i++)
            {
                BaseStats[i] = myBase.Stats[i];
                Stat[i] = new Combat.Stat((Stat)i, this);
            }

            var spellSlot = 0;
            for (var I = 0; I < Base.Spells.Count; I++)
            {
                var slot = new SpellSlot(spellSlot);
                slot.Set(new Spell(Base.Spells[I]));
                Spells.Add(slot);
                spellSlot++;
            }

            //Give NPC Drops
            var itemSlot = 0;
            foreach (var drop in myBase.Drops)
            {
                var slot = new InventorySlot(itemSlot);
                slot.Set(new Item(drop.ItemId, drop.Quantity));
                slot.DropChance = drop.Chance;
                Items.Add(slot);
                itemSlot++;
            }

            for (var i = 0; i < (int)Vital.VitalCount; i++)
            {
                SetMaxVital(i, myBase.MaxVital[i]);
                SetVital(i, myBase.MaxVital[i]);
            }

            _range = (byte)myBase.SightRange;
            _moveRange = (byte)Randomization.Next(myBase.SightRange / 2, myBase.SightRange);
            _pathFinder = new Pathfinder(this);
        }

        public NpcBase Base { get; private set; }

        private bool IsStunnedOrSleeping => CachedStatuses.Any(PredicateStunnedOrSleeping);

        private bool IsUnableToCastSpells => CachedStatuses.Any(PredicateUnableToCastSpells);

        public override EntityType GetEntityType()
        {
            return EntityType.GlobalEntity;
        }

        public override void Die(bool generateLoot = true, Entity killer = null)
        {
            lock (EntityLock)
            {
                base.Die(generateLoot, killer);
                ResetAggroCenter();

                if (MapController.TryGetInstanceFromMap(MapId, MapInstanceId, out var instance))
                {
                    instance.RemoveEntity(this);
                }

                PacketSender.SendEntityDie(this);
                PacketSender.SendEntityLeave(this);
            }
        }

        protected override bool ShouldDropItem(Entity killer, ItemBase itemDescriptor, Item item, float dropRateModifier, out Guid lootOwner)
        {
            lootOwner = (killer as Player)?.Id ?? Id;
            return base.ShouldDropItem(killer, itemDescriptor, item, dropRateModifier, out _);
        }

        private static bool TargetHasStealth(Entity target)
        {
            return target == null || target.CachedStatuses.Any(s => s.Type == SpellEffect.Stealth);
        }

        private bool IsTargetWithinAggroCenter(PathfinderTarget pathTarget)
        {
            return pathTarget != null &&
                   _aggroCenterMap != null &&
                   (pathTarget.TargetMapId == _aggroCenterMap.Id ||
                   pathTarget.TargetX == _aggroCenterX ||
                   pathTarget.TargetY == _aggroCenterY);
        }

        //Targeting
        public void AssignTarget(Entity en)
        {
            var oldTarget = Target;

            // Are we resetting? If so, do not allow for a new target.
            var pathTarget = _pathFinder?.GetTarget();
            if (IsTargetWithinAggroCenter(pathTarget))
            {
                return;
            }

            //Why are we doing all of this logic if we are assigning a target that we already have?
            if (en != null && en != Target)
            {
                // Can't assign a new target if taunted, unless we're resetting their target somehow.
                // Also make sure the taunter is in range.. If they're dead or gone, we go for someone else!
                if (IsTargetWithinAggroCenter(pathTarget))
                {
                    foreach (var status in CachedStatuses)
                    {
                        if (en != status.Attacker &&
                            status.Type == SpellEffect.Taunt &&
                            GetDistanceTo(status.Attacker) != 9999)
                        {
                            return;
                        }
                    }
                }

                if (en is Projectile projectile)
                {
                    if (projectile.Owner != this && !TargetHasStealth(projectile))
                    {
                        Target = projectile.Owner;
                    }
                }
                else
                {
                    if (en is Npc npc)
                    {
                        if (npc.Base == Base)
                        {
                            if (Base.AttackAllies == false)
                            {
                                return;
                            }
                        }
                    }

                    if (en is Player)
                    {
                        //TODO Make sure that the npc can target the player
                        if (this != en && !TargetHasStealth(en))
                        {
                            Target = en;
                        }
                    }
                    else
                    {
                        if (this != en && !TargetHasStealth(en))
                        {
                            Target = en;
                        }
                    }
                }

                // Are we configured to handle resetting NPCs after they chase a target for a specified amount of tiles?
                if (Options.Npc.AllowResetRadius)
                {
                    // Are we configured to allow new reset locations before they move to their original location, or do we simply not have an original location yet?
                    if (Options.Npc.AllowNewResetLocationBeforeFinish || _aggroCenterMap == null)
                    {
                        _aggroCenterMap = Map;
                        _aggroCenterX = X;
                        _aggroCenterY = Y;
                        _aggroCenterZ = Z;
                    }
                }
            }
            else
            {
                Target = en;
            }

            if (Target != oldTarget)
            {
                CombatTimer = Timing.Global.Milliseconds + Options.CombatTime;
                PacketSender.SendNpcAggressionToProximity(this);
            }

            _targetFailCounter = 0;
        }

        public void RemoveFromDamageMap(Entity en)
        {
            DamageMap.TryRemove(en, out _);
        }

        public void RemoveTarget()
        {
            AssignTarget(null);
        }

        public override int CalculateAttackTime()
        {
            if (Base.AttackSpeedModifier == 1) //Static
            {
                return Base.AttackSpeedValue;
            }

            return base.CalculateAttackTime();
        }

        public override bool CanAttack(Entity entity, SpellBase spell)
        {
            if (!base.CanAttack(entity, spell))
            {
                return false;
            }

            if (entity is EventPageInstance)
            {
                return false;
            }

            //Check if the attacker is stunned or blinded.
            foreach (var status in CachedStatuses)
            {
                if (status.Type == SpellEffect.Stun || status.Type == SpellEffect.Sleep)
                {
                    return false;
                }
            }

            if (TargetHasStealth(entity))
            {
                return false;
            }

            if (entity is Resource)
            {
                if (!entity.Passable)
                {
                    return false;
                }
            }
            else if (entity is Npc)
            {
                return CanNpcCombat(entity, spell != null && spell.Combat.Friendly) || entity == this;
            }
            else if (entity is Player player)
            {
                var friendly = spell != null && spell.Combat.Friendly;
                if (friendly && IsAllyOf(player))
                {
                    return true;
                }

                if (!friendly && !IsAllyOf(player))
                {
                    return true;
                }

                return false;
            }

            return true;
        }

        public override void TryAttack(Entity target)
        {
            if (target.IsDisposed)
            {
                return;
            }

            if (!CanAttack(target, null))
            {
                return;
            }

            if (!IsOneBlockAway(target))
            {
                return;
            }

            if (!IsFacingTarget(target))
            {
                return;
            }

            var deadAnimations = new List<KeyValuePair<Guid, Direction>>();
            var aliveAnimations = new List<KeyValuePair<Guid, Direction>>();

            //We were forcing at LEAST 1hp base damage.. but then you can't have guards that won't hurt the player.
            //https://www.ascensiongamedev.com/community/bug_tracker/intersect/npc-set-at-0-attack-damage-still-damages-player-by-1-initially-r915/
            if (IsAttacking)
            {
                return;
            }

            if (Base.AttackAnimation != null)
            {
                PacketSender.SendAnimationToProximity(
                    Base.AttackAnimationId, -1, Guid.Empty, target.MapId, (byte)target.X, (byte)target.Y,
                    Dir, target.MapInstanceId
                );
            }

            base.TryAttack(
                target, Base.Damage, (DamageType)Base.DamageType, (Stat)Base.ScalingStat, Base.Scaling,
                Base.CritChance, Base.CritMultiplier, deadAnimations, aliveAnimations
            );

            PacketSender.SendEntityAttack(this, CalculateAttackTime());
        }

        public bool CanNpcCombat(Entity enemy, bool friendly = false)
        {
            //Check for NpcVsNpc Combat, both must be enabled and the attacker must have it as an enemy or attack all types of npc.
            if (!friendly)
            {
                if (enemy != null && enemy is Npc enemyNpc && Base != null)
                {
                    if (enemyNpc.Base.NpcVsNpcEnabled == false)
                    {
                        return false;
                    }

                    if (Base.AttackAllies && enemyNpc.Base == Base)
                    {
                        return true;
                    }

                    for (var i = 0; i < Base.AggroList.Count; i++)
                    {
                        if (NpcBase.Get(Base.AggroList[i]) == enemyNpc.Base)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                if (enemy is Player)
                {
                    return true;
                }
            }
            else if (enemy is Npc enemyNpc && Base != null && enemyNpc.Base == Base && Base.AttackAllies == false)
            {
                return true;
            }
            else if (enemy is Player)
            {
                return false;
            }

            return false;
        }

        private static bool PredicateStunnedOrSleeping(Status status)
        {
            switch (status?.Type)
            {
                case SpellEffect.Sleep:
                case SpellEffect.Stun:
                    return true;

                case SpellEffect.Silence:
                case SpellEffect.None:
                case SpellEffect.Snare:
                case SpellEffect.Blind:
                case SpellEffect.Stealth:
                case SpellEffect.Transform:
                case SpellEffect.Cleanse:
                case SpellEffect.Invulnerable:
                case SpellEffect.Shield:
                case SpellEffect.OnHit:
                case SpellEffect.Taunt:
                case null:
                    return false;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static bool PredicateUnableToCastSpells(Status status)
        {
            switch (status?.Type)
            {
                case SpellEffect.Silence:
                case SpellEffect.Sleep:
                case SpellEffect.Stun:
                    return true;

                case SpellEffect.None:
                case SpellEffect.Snare:
                case SpellEffect.Blind:
                case SpellEffect.Stealth:
                case SpellEffect.Transform:
                case SpellEffect.Cleanse:
                case SpellEffect.Invulnerable:
                case SpellEffect.Shield:
                case SpellEffect.OnHit:
                case SpellEffect.Taunt:
                case null:
                    return false;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public override TileType MovesTo(Direction moveDir)
        {
            var movesTo = base.MovesTo(moveDir);

            // If configured & blocked by an entity, ignore the entity and proceed to move
            if (Options.Instance.NpcOpts.IntangibleDuringReset && movesTo > TileType.Clear)
            {
                movesTo = _resetting ? TileType.Clear : movesTo;
            }

            if ((movesTo != TileType.Clear && movesTo != TileType.Slide) || !IsFleeing() ||
                !Options.Instance.NpcOpts.AllowResetRadius)
            {
                return movesTo;
            }

            var yOffset = 0;
            var xOffset = 0;

            var tile = new TileHelper(MapId, X, Y);
            switch (moveDir)
            {
                case Direction.Up:
                    yOffset--;
                    break;

                case Direction.Down:
                    yOffset++;
                    break;

                case Direction.Left:
                    xOffset--;
                    break;

                case Direction.Right:
                    xOffset++;
                    break;

                case Direction.UpLeft:
                    yOffset--;
                    xOffset--;
                    break;

                case Direction.UpRight:
                    yOffset--;
                    xOffset++;
                    break;

                case Direction.DownLeft:
                    yOffset++;
                    xOffset--;
                    break;

                case Direction.DownRight:
                    yOffset++;
                    xOffset++;
                    break;
            }

            if (tile.Translate(xOffset, yOffset))
            {
                //If this would move us past our reset radius then we cannot move.
                var dist = GetDistanceBetween(_aggroCenterMap, tile.GetMap(), _aggroCenterX, tile.GetX(), _aggroCenterY,
                    tile.GetY());
                if (dist > Math.Max(Options.Npc.ResetRadius, Base.ResetRadius))
                {
                    return TileType.Block;
                }
            }

            return movesTo;
        }

        private void TryCastSpells()
        {
            var target = Target;

            if (target == null || _pathFinder.GetTarget() == null)
            {
                return;
            }

            // Check if NPC is stunned/sleeping
            if (IsStunnedOrSleeping)
            {
                return;
            }

            //Check if NPC is casting a spell
            if (IsCasting)
            {
                return; //can't move while casting
            }

            if (_castFreq >= Timing.Global.Milliseconds)
            {
                return;
            }

            // Check if the NPC is able to cast spells
            if (IsUnableToCastSpells)
            {
                return;
            }

            if (Base.Spells == null || Base.Spells.Count <= 0)
            {
                return;
            }

            // Pick a random spell
            var spellIndex = Randomization.Next(0, Spells.Count);
            var spellId = Base.Spells[spellIndex];
            var spellBase = SpellBase.Get(spellId);
            if (spellBase == null)
            {
                return;
            }

            if (spellBase.Combat == null)
            {
                Log.Warn($"Combat data missing for {spellBase.Id}.");
            }

            // Check if we are even allowed to cast this spell.
            if (!CanCastSpell(spellBase, target, true, out var _))
            {
                return;
            }

            var targetType = spellBase.Combat?.TargetType ?? SpellTargetType.Single;
            var projectileBase = spellBase.Combat?.Projectile;

            if (spellBase.SpellType == SpellType.CombatSpell &&
                targetType == SpellTargetType.Projectile &&
                projectileBase != null &&
                InRangeOf(target, projectileBase.Range))
            {
                var dirToEnemy = DirToEnemy(target);
                if (dirToEnemy != Dir)
                {
                    if (_lastRandomMove >= Timing.Global.Milliseconds)
                    {
                        return;
                    }

                    //Face the target -- next frame fire -- then go on with life
                    ChangeDir(dirToEnemy); // Gotta get dir to enemy
                    _lastRandomMove = Timing.Global.Milliseconds + Randomization.Next(1000, 3000);
                    return;
                }
            }

            CastTime = Timing.Global.Milliseconds + spellBase.CastDuration;

            if ((spellBase.Combat?.Friendly ?? false) && spellBase.SpellType != SpellType.WarpTo)
            {
                CastTarget = this;
            }
            else
            {
                CastTarget = target;
            }

            switch (Base.SpellFrequency)
            {
                case 0:
                    _castFreq = Timing.Global.Milliseconds + 30000;
                    break;

                case 1:
                    _castFreq = Timing.Global.Milliseconds + 15000;
                    break;

                case 2:
                    _castFreq = Timing.Global.Milliseconds + 8000;
                    break;

                case 3:
                    _castFreq = Timing.Global.Milliseconds + 4000;
                    break;

                case 4:
                    _castFreq = Timing.Global.Milliseconds + 2000;
                    break;
            }

            SpellCastSlot = spellIndex;

            if (spellBase.CastAnimationId != Guid.Empty)
            {
                PacketSender.SendAnimationToProximity(spellBase.CastAnimationId, 1, Id, MapId, 0, 0, Dir, MapInstanceId);

                //Target Type 1 will be global entity
            }

            PacketSender.SendEntityCastTime(this, spellId);
        }

        private bool IsFleeing()
        {
            if (Base.FleeHealthPercentage <= 0)
            {
                return false;
            }

            var fleeHpCutoff = GetMaxVital(Vital.Health) * (Base.FleeHealthPercentage / 100f);
            return GetVital(Vital.Health) < fleeHpCutoff;
        }

        // TODO: Improve NPC movement to be more fluid like a player
        //General Updating
        public override void Update(long timeMs)
        {
            var lockObtained = false;
            try
            {
                Monitor.TryEnter(EntityLock, ref lockObtained);
                if (!lockObtained)
                {
                    return;
                }

                var curMapLink = MapId;
                base.Update(timeMs);
                var tempTarget = Target;

                foreach (var status in CachedStatuses)
                {
                    if (status.Type == SpellEffect.Stun || status.Type == SpellEffect.Sleep)
                    {
                        return;
                    }
                }

                var fleeing = IsFleeing();

                if (MoveTimer < Timing.Global.Milliseconds)
                {
                    var targetMap = Guid.Empty;
                    var targetX = 0;
                    var targetY = 0;
                    var targetZ = 0;

                    //TODO Clear Damage Map if out of combat (target is null and combat timer is to the point that regen has started)
                    if (tempTarget != null && (Options.Instance.NpcOpts.ResetIfCombatTimerExceeded &&
                                               Timing.Global.Milliseconds > CombatTimer))
                    {
                        if (CheckForResetLocation(true))
                        {
                            if (Target != tempTarget)
                            {
                                PacketSender.SendNpcAggressionToProximity(this);
                            }

                            return;
                        }
                    }

                    // Are we resetting? If so, regenerate completely!
                    if (_resetting)
                    {
                        var distance = GetDistanceTo(_aggroCenterMap, _aggroCenterX, _aggroCenterY);

                        // Have we reached our destination? If so, clear it.
                        if (distance < 1)
                        {
                            targetMap = Guid.Empty;
                            ResetAggroCenter();
                        }

                        Reset(Options.Instance.NpcOpts.ContinuouslyResetVitalsAndStatuses);
                        tempTarget = Target;

                        if (distance != _resetDistance)
                        {
                            _resetDistance = distance;
                        }
                        else
                        {
                            // Something is fishy here.. We appear to be stuck in a reset loop?
                            // Give it a few more attempts and reset the NPC's center if we're stuck!
                            _resetCounter++;
                            if (_resetCounter > ResetMax)
                            {
                                targetMap = Guid.Empty;
                                ResetAggroCenter();
                                _resetCounter = 0;
                                _resetDistance = 0;
                            }
                        }
                    }

                    if (tempTarget != null && (tempTarget.IsDead() || !InRangeOf(tempTarget, Options.MapWidth * 2)))
                    {
                        TryFindNewTarget(Timing.Global.Milliseconds, tempTarget.Id);
                        tempTarget = Target;
                    }

                    //Check if there is a target, if so, run their ass down.
                    if (tempTarget != null)
                    {
                        if (!tempTarget.IsDead() && CanAttack(tempTarget, null))
                        {
                            targetMap = tempTarget.MapId;
                            targetX = tempTarget.X;
                            targetY = tempTarget.Y;
                            targetZ = tempTarget.Z;
                            foreach (var targetStatus in tempTarget.CachedStatuses)
                            {
                                if (targetStatus.Type == SpellEffect.Stealth)
                                {
                                    targetMap = Guid.Empty;
                                    targetX = 0;
                                    targetY = 0;
                                    targetZ = 0;
                                }
                            }
                        }
                    }
                    else //Find a target if able
                    {
                        // Check if attack on sight or have other npc's to target
                        TryFindNewTarget(timeMs);
                        tempTarget = Target;
                    }

                    if (targetMap != Guid.Empty)
                    {
                        //Check if target map is on one of the surrounding maps, if not then we are not even going to look.
                        if (targetMap != MapId)
                        {
                            var found = false;
                            foreach (var map in MapController.Get(MapId).SurroundingMaps)
                            {
                                if (map.Id == targetMap)
                                {
                                    found = true;
                                    break;
                                }
                            }

                            if (!found)
                            {
                                targetMap = Guid.Empty;
                                _moveTargetMap = Guid.Empty;
                            }
                        }
                    }

                    if (targetMap != Guid.Empty)
                    {
                        if (_pathFinder.GetTarget() != null)
                        {
                            if (targetMap != _pathFinder.GetTarget().TargetMapId ||
                                targetX != _pathFinder.GetTarget().TargetX ||
                                targetY != _pathFinder.GetTarget().TargetY)
                            {
                                _pathFinder.SetTarget(null);
                            }
                        }

                        if (_pathFinder.GetTarget() == null)
                        {
                            _pathFinder.SetTarget(new PathfinderTarget(targetMap, targetX, targetY, targetZ));

                            if (tempTarget != Target)
                            {
                                tempTarget = Target;
                            }
                        }
                    }

                    if (_pathFinder.GetTarget() != null && Base.Movement != (int)NpcMovement.Static)
                    {
                        TryCastSpells();

                        // TODO: Make resetting mobs actually return to their starting location.
                        if ((!_resetting && !IsOneBlockAway(
                                _pathFinder.GetTarget().TargetMapId, _pathFinder.GetTarget().TargetX,
                                _pathFinder.GetTarget().TargetY, _pathFinder.GetTarget().TargetZ
                            )) ||
                            (_resetting && GetDistanceTo(_aggroCenterMap, _aggroCenterX, _aggroCenterY) != 0)
                           )
                        {
                            switch (_pathFinder.Update(timeMs))
                            {
                                case PathfinderResult.Success:
                                    var dir = _pathFinder.GetMove();
                                    
                                    if (dir > Direction.None)
                                    {
                                        if (fleeing)
                                        {
                                            switch (dir)
                                            {
                                                case Direction.Up:
                                                    dir = Direction.Down;
                                                    break;

                                                case Direction.Down:
                                                    dir = Direction.Up;
                                                    break;

                                                case Direction.Left:
                                                    dir = Direction.Right;
                                                    break;

                                                case Direction.Right:
                                                    dir = Direction.Left;
                                                    break;

                                                case Direction.UpLeft:
                                                    dir = Direction.UpRight;
                                                    break;

                                                case Direction.UpRight:
                                                    dir = Direction.UpLeft;
                                                    break;

                                                case Direction.DownRight:
                                                    dir = Direction.DownLeft;
                                                    break;

                                                case Direction.DownLeft:
                                                    dir = Direction.DownRight;
                                                    break;
                                            }
                                        }

                                        if (CanMoveTo(dir))
                                        {
                                            Move(dir, null);
                                        }
                                        else
                                        {
                                            _pathFinder.PathFailed(timeMs);
                                        }

                                        // Are we resetting?
                                        if (_resetting)
                                        {
                                            // Have we reached our destination? If so, clear it.
                                            if (GetDistanceTo(_aggroCenterMap, _aggroCenterX, _aggroCenterY) == 0)
                                            {
                                                targetMap = Guid.Empty;
                                                _moveTargetMap = Guid.Empty;
                                                ResetAggroCenter();
                                            }
                                        }
                                    }

                                    break;

                                case PathfinderResult.OutOfRange:
                                case PathfinderResult.NoPathToTarget:
                                case PathfinderResult.Failure:
                                    targetMap = Guid.Empty;
                                    _moveTargetMap = Guid.Empty;
                                    TryFindNewTarget(timeMs, tempTarget?.Id ?? Guid.Empty, true);
                                    break;

                                case PathfinderResult.Wait:
                                    targetMap = Guid.Empty;
                                    _moveTargetMap = Guid.Empty;
                                    break;

                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        }
                        else
                        {
                            var fled = false;
                            if (tempTarget != null && fleeing)
                            {
                                var dir = DirToEnemy(tempTarget);
                                switch (dir)
                                {
                                    case Direction.Up:
                                        dir = Direction.Down;
                                        break;

                                    case Direction.Down:
                                        dir = Direction.Up;
                                        break;

                                    case Direction.Left:
                                        dir = Direction.Right;
                                        break;

                                    case Direction.Right:
                                        dir = Direction.Left;
                                        break;

                                    case Direction.UpLeft:
                                        dir = Direction.UpRight;
                                        break;

                                    case Direction.UpRight:
                                        dir = Direction.UpLeft;
                                        break;

                                    case Direction.DownRight:
                                        dir = Direction.DownLeft;
                                        break;

                                    case Direction.DownLeft:
                                        dir = Direction.DownRight;
                                        break;
                                }

                                if (CanMoveTo(dir))
                                {
                                    Move(dir, null);
                                    fled = true;
                                }
                            }

                            if (!fled && tempTarget != null)
                            {
                                if (Dir != DirToEnemy(tempTarget) && DirToEnemy(tempTarget) != Direction.None)
                                {
                                    ChangeDir(DirToEnemy(tempTarget));
                                }
                                else
                                {
                                    if (tempTarget.IsDisposed)
                                    {
                                        _moveTargetMap = Guid.Empty;
                                        TryFindNewTarget(timeMs);
                                    }
                                    else
                                    {
                                        if (CanAttack(tempTarget, null))
                                        {
                                            TryAttack(tempTarget);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    CheckForResetLocation();

                    //Move randomly
                    if (targetMap != Guid.Empty)
                    {
                        return;
                    }

                    if (_lastRandomMove >= Timing.Global.Milliseconds || IsCasting)
                    {
                        return;
                    }

                    switch (Base.Movement)
                    {
                        case (int)NpcMovement.MoveRandomly:
                            MoveRandomly();
                            break;

                        case (int)NpcMovement.TurnRandomly:
                            ChangeDir(Randomization.NextDirection());
                            break;

                        case (int)NpcMovement.StandStill:
                        case (int)NpcMovement.Static:
                            break;
                    }

                    _lastRandomMove = Timing.Global.Milliseconds + Randomization.Next(1000, 3000);

                    if (fleeing)
                    {
                        _lastRandomMove = Timing.Global.Milliseconds + (long)GetMovementTime();
                    }
                }

                //If we switched maps, lets update the maps
                if (curMapLink != MapId)
                {
                    if (curMapLink == Guid.Empty)
                    {
                        if (MapController.TryGetInstanceFromMap(curMapLink, MapInstanceId, out var instance))
                        {
                            instance.RemoveEntity(this);
                        }
                    }

                    if (MapId != Guid.Empty)
                    {
                        if (MapController.TryGetInstanceFromMap(MapId, MapInstanceId, out var instance))
                        {
                            instance.AddEntity(this);
                        }
                    }
                }
            }
            finally
            {
                if (lockObtained)
                {
                    Monitor.Exit(EntityLock);
                }
            }
        }

        private void MoveRandomly()
        {
            if (_moveTargetMap == Guid.Empty)
            {
                var randomMoveRange = Randomization.Next(-_moveRange, _moveRange);
                var tile = new TileHelper(MapId, X, Y);

                if (tile.Translate(randomMoveRange, randomMoveRange))
                {
                    _moveTargetMap = tile.GetMapId();
                    _moveTarget.X = tile.GetX();
                    _moveTarget.Y = tile.GetY();
                }
                else
                {
                    _moveTargetMap = Guid.Empty;
                    _moveTarget.X = 0;
                    _moveTarget.Y = 0;
                }
            }
            else
            {
                _pathFinder.SetTarget(new PathfinderTarget(_moveTargetMap, _moveTarget.X, _moveTarget.Y, Z));
            }

            var direction = Randomization.NextDirection();

            if (CanMoveTo(direction))
            {
                Move(direction, null);
            }
        }

        private bool CanMoveTo(Direction direction)
        {
            if (MovesTo(direction) == TileType.Clear || MovesTo(direction) == TileType.Slide)
            {
                foreach (var status in CachedStatuses)
                {
                    if (status.Type == SpellEffect.Stun ||
                        status.Type == SpellEffect.Snare ||
                        status.Type == SpellEffect.Sleep ||
                        status.Type == SpellEffect.Knockback)
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Resets the NPCs "Aggro Center".
        /// </summary>
        private void ResetAggroCenter()
        {
            _aggroCenterMap = null;
            _aggroCenterX = 0;
            _aggroCenterY = 0;
            _aggroCenterZ = 0;
            _pathFinder?.SetTarget(null);
            _resetting = false;
        }

        private bool ShouldResetLocation(bool forceDistance)
        {
            if (!Options.Npc.AllowResetRadius || _aggroCenterMap == null)
            {
                return false;
            }

            var resetRadius = Math.Max(Options.Npc.ResetRadius,
                Math.Min(Base.ResetRadius, Math.Max(Options.MapWidth, Options.MapHeight)));
            var distanceToAggroCenter = GetDistanceTo(_aggroCenterMap, _aggroCenterX, _aggroCenterY);
            bool withinResetRadius = distanceToAggroCenter <= resetRadius && !forceDistance;

            return !withinResetRadius;
        }

        private bool CheckForResetLocation(bool forceDistance = false)
        {
            // Check if we've moved out of our range we're allowed to move from after being "aggro'd" by something.
            // If so, remove target and move back to the origin point.
            if (!ShouldResetLocation(forceDistance))
            {
                return false;
            }

            Reset(Options.Npc.ResetVitalsAndStatusses);

            _resetCounter = 0;
            _resetDistance = 0;

            // Try and move back to where we came from before we started chasing something.
            _resetting = true;
            _pathFinder.SetTarget(new PathfinderTarget(_aggroCenterMap.Id, _aggroCenterX, _aggroCenterY,
                _aggroCenterZ));
            return true;
        }

        private void Reset(bool resetVitals, bool clearLocation = false)
        {
            // Remove our target.
            RemoveTarget();

            DamageMap.Clear();
            LootMap.Clear();
            LootMapCache = Array.Empty<Guid>();

            if (clearLocation)
            {
                ResetAggroCenter();
            }

            // Reset our vitals and statuses when configured.
            if (!resetVitals)
            {
                return;
            }

            Statuses.Clear();
            CachedStatuses = Statuses.Values.ToArray();
            DoT.Clear();
            CachedDots = DoT.Values.ToArray();
            for (var v = 0; v < (int)Vital.VitalCount; v++)
            {
                RestoreVital((Vital)v);
            }
        }

        // Completely resets an Npc to full health and its spawnpoint if it's current chasing something.
        public override void Reset()
        {
            if (_aggroCenterMap != null)
            {
                Warp(_aggroCenterMap.Id, _aggroCenterX, _aggroCenterY);
            }
            
            Reset(true, true);
        }

        public override void NotifySwarm(Entity attacker)
        {
            if (!MapController.TryGetInstanceFromMap(MapId, MapInstanceId, out var instance))
            {
                return;
            }

            foreach (var en in instance.GetEntities(true))
            {
                if (!(en is Npc npc))
                {
                    continue;
                }

                if (!(npc.Target == null & npc.Base.Swarm) || npc.Base != Base)
                {
                    continue;
                }

                if (npc.InRangeOf(attacker, npc.Base.SightRange))
                {
                    npc.AssignTarget(attacker);
                }
            }
        }

        public bool CanPlayerAttack(Player en)
        {
            //Check to see if the npc is a friend/protector...
            if (IsAllyOf(en))
            {
                return false;
            }

            //If not then check and see if player meets the conditions to attack the npc...
            return Base.PlayerCanAttackConditions.Lists.Count == 0 ||
                   Conditions.MeetsConditionLists(Base.PlayerCanAttackConditions, en, null);
        }

        public override bool IsAllyOf(Entity otherEntity)
        {
            switch (otherEntity)
            {
                case Npc otherNpc:
                    return Base == otherNpc.Base;

                case Player otherPlayer:
                    var conditionLists = Base.PlayerFriendConditions;
                    return (conditionLists?.Count ?? 0) != 0 &&
                           Conditions.MeetsConditionLists(conditionLists, otherPlayer, null);

                default:
                    return base.IsAllyOf(otherEntity);
            }
        }

        private bool ShouldAttackPlayerOnSight(Player en)
        {
            if (IsAllyOf(en))
            {
                return false;
            }

            if (Base.Aggressive)
            {
                return Base.AttackOnSightConditions.Lists.Count <= 0 ||
                       !Conditions.MeetsConditionLists(Base.AttackOnSightConditions, en, null);
            }

            return Base.AttackOnSightConditions.Lists.Count > 0 &&
                   Conditions.MeetsConditionLists(Base.AttackOnSightConditions, en, null);
        }

        public void TryFindNewTarget(long timeMs, Guid avoidId = new Guid(), bool ignoreTimer = false, Entity attackedBy = null)
        {
            if (!ignoreTimer && _findTargetWaitTime > timeMs)
            {
                return;
            }

            // Are we resetting? If so, do not allow for a new target.
            var pathTarget = _pathFinder?.GetTarget();
            if (IsTargetWithinAggroCenter(pathTarget))
            {
                if (!Options.Instance.NpcOpts.AllowEngagingWhileResetting || attackedBy == null ||
                    attackedBy.GetDistanceTo(_aggroCenterMap, _aggroCenterX, _aggroCenterY) >
                    Math.Max(Options.Instance.NpcOpts.ResetRadius, Base.ResetRadius))
                {
                    return;
                }

                // We're resetting and just got attacked, and we allow re-engagement.. let's stop resetting and fight!
                _pathFinder?.SetTarget(null);
                _resetting = false;
                AssignTarget(attackedBy);
                return;
            }

            var possibleTargets = new List<Entity>();
            var closestRange = _range + 1; //If the range is out of range we didn't find anything.
            var closestIndex = -1;
            var highestDmgIndex = -1;
           
            if (DamageMap.Count > 0)
            {
                // Go through all of our potential targets in order of damage done as instructed and select the first matching one.
                long highestDamage = 0;
                foreach (var en in DamageMap.ToArray())
                {
                    // Are we supposed to avoid this one?
                    if (en.Key.Id == avoidId)
                    {
                        continue;
                    }
                    
                    // Is this entry dead?, if so skip it.
                    if (en.Key.IsDead())
                    {
                        continue;   
                    }

                    // Is this entity on our instance anymore? If not skip it, but don't remove it in case they come back and need item drop determined
                    if (en.Key.MapInstanceId != MapInstanceId)
                    {
                        continue;
                    }

                    // Are we at a valid distance? (9999 means not on this map or somehow null!)
                    if (GetDistanceTo(en.Key) != 9999)
                    {
                        possibleTargets.Add(en.Key);

                        // Do we have the highest damage?
                        if (en.Value > highestDamage)
                        {
                            highestDmgIndex = possibleTargets.Count - 1;
                            highestDamage = en.Value;
                        }    
                        
                    }
                }
            }

            // Scan for nearby targets
            foreach (var instance in MapController.GetSurroundingMapInstances(MapId, MapInstanceId, true))
            {
                foreach (var entity in instance.GetCachedEntities())
                {
                    if (entity == null || entity.IsDead() || entity == this || entity.Id == avoidId)
                    {
                        continue;
                    }

                    //TODO Check if NPC is allowed to attack player with new conditions
                    if (entity is Player player)
                    {
                        // Are we aggressive towards this player or have they hit us?
                        if (!ShouldAttackPlayerOnSight(player) &&
                            (!DamageMap.ContainsKey(entity) || entity.MapInstanceId != MapInstanceId))
                        {
                            continue;
                        }

                        var dist = GetDistanceTo(entity);
                        if (dist <= _range && dist < closestRange)
                        {
                            possibleTargets.Add(entity);
                            closestIndex = possibleTargets.Count - 1;
                            closestRange = dist;
                        }
                    }
                    else if (entity is Npc npc)
                    {
                        if (!Base.Aggressive || !Base.AggroList.Contains(npc.Base.Id))
                        {
                            continue;
                        }

                        var dist = GetDistanceTo(entity);
                        if (dist <= _range && dist < closestRange)
                        {
                            possibleTargets.Add(entity);
                            closestIndex = possibleTargets.Count - 1;
                            closestRange = dist;
                        }
                    }
                }
            }

            // Assign our target if we've found one!
            if (Base.FocusHighestDamageDealer && highestDmgIndex != -1)
            {
                // We're focussed on whoever has the most threat! o7
                AssignTarget(possibleTargets[highestDmgIndex]);
            }
            else if (Target != null && possibleTargets.Count > 0)
            {
                // Time to randomize who we target.. Since we don't actively care who we attack!
                // 10% chance to just go for someone else.
                if (Randomization.Next(1, 101) > 90)
                {
                    if (possibleTargets.Count > 1)
                    {
                        var target = Randomization.Next(0, possibleTargets.Count - 1);
                        AssignTarget(possibleTargets[target]);
                    }
                    else
                    {
                        AssignTarget(possibleTargets[0]);
                    }
                }
            }
            else if (Target == null && Base.Aggressive && closestIndex != -1)
            {
                // Aggressively attack closest person!
                AssignTarget(possibleTargets[closestIndex]);
            }
            else if (possibleTargets.Count > 0)
            {
                // Not aggressive but no target, so just try and attack SOMEONE on the damage table!
                if (possibleTargets.Count > 1)
                {
                    var target = Randomization.Next(0, possibleTargets.Count - 1);
                    AssignTarget(possibleTargets[target]);
                }
                else
                {
                    AssignTarget(possibleTargets[0]);
                }
            }
            else
            {
                // We can't find a valid target somehow, keep it up a few times and reset if this keeps failing!
                _targetFailCounter += 1;
                if (_targetFailCounter > TargetFailMax)
                {
                    CheckForResetLocation(true);
                }
            }

            _findTargetWaitTime = timeMs + FindTargetDelay;
        }

        public override void ProcessRegen()
        {
            if (Base == null)
            {
                return;
            }

            foreach (Vital vital in Enum.GetValues(typeof(Vital)))
            {
                if (vital >= Vital.VitalCount)
                {
                    continue;
                }

                var vitalId = (int)vital;
                var vitalValue = GetVital(vital);
                var maxVitalValue = GetMaxVital(vital);
                if (vitalValue >= maxVitalValue)
                {
                    continue;
                }

                var vitalRegenRate = Base.VitalRegen[vitalId] / 100f;
                var regenValue = (int)Math.Max(1, maxVitalValue * vitalRegenRate) *
                                 Math.Abs(Math.Sign(vitalRegenRate));

                AddVital(vital, regenValue);
            }
        }

        public override void Warp(Guid newMapId,
            float newX,
            float newY,
            Direction newDir,
            bool adminWarp = false,
            int zOverride = 0,
            bool mapSave = false,
            bool fromWarpEvent = false,
            MapInstanceType? mapInstanceType = null,
            bool fromLogin = false,
            bool forceInstanceChange = false)
        {
            if (!MapController.TryGetInstanceFromMap(newMapId, MapInstanceId, out var newMap))
            {
                return;
            }

            if (!MapController.TryGetInstanceFromMap(MapId, MapInstanceId, out var oldMap))
            {
                return;
            }

            X = (int)newX;
            Y = (int)newY;
            Z = zOverride;
            Dir = newDir;
            if (newMapId != MapId)
            {
                oldMap.RemoveEntity(this);
                PacketSender.SendEntityLeave(this);
                MapId = newMapId;
            }
            else
            {
                newMap.AddEntity(this);
                PacketSender.SendEntityDataToProximity(this);
                PacketSender.SendEntityPositionToAll(this);
                PacketSender.SendEntityStats(this);
            }
        }

        /// <summary>
        /// Determines the aggression of this NPC towards a player.
        /// </summary>
        /// <param name="player">The player object to evaluate the relationship with.</param>
        /// <returns>The NPC's behavior towards the player.</returns>
        public NpcBehavior GetAggression(Player player)
        {
            if (this.Target != null)
            {
                return NpcBehavior.Aggressive;
            }

            var ally = IsAllyOf(player);
            var attackOnSight = ShouldAttackPlayerOnSight(player);
            var canPlayerAttack = CanPlayerAttack(player);

            if (ally && !canPlayerAttack)
            {
                return NpcBehavior.Guard;
            }

            if (attackOnSight)
            {
                return NpcBehavior.AttackOnSight;
            }

            if (!ally && !attackOnSight && canPlayerAttack)
            {
                return NpcBehavior.AttackWhenAttacked;
            }

            if (!ally && !attackOnSight && !canPlayerAttack)
            {
                return NpcBehavior.Neutral;
            }

            return NpcBehavior.Neutral;
        }

        public override EntityPacket EntityPacket(EntityPacket packet = null, Player forPlayer = null)
        {
            if (packet == null)
            {
                packet = new NpcEntityPacket();
            }

            packet = base.EntityPacket(packet, forPlayer);

            var pkt = (NpcEntityPacket) packet;
            pkt.Aggression = GetAggression(forPlayer);

            return pkt;
        }

    }

}
