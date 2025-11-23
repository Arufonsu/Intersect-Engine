using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Intersect.Client.Core;
using Intersect.Client.Entities.Events;
using Intersect.Client.Entities.Projectiles;
using Intersect.Client.Framework.Content;
using Intersect.Client.Framework.Entities;
using Intersect.Client.Framework.GenericClasses;
using Intersect.Client.Framework.Graphics;
using Intersect.Client.Framework.Items;
using Intersect.Client.Framework.Maps;
using Intersect.Client.General;
using Intersect.Client.Items;
using Intersect.Client.Localization;
using Intersect.Client.Maps;
using Intersect.Client.Spells;
using Intersect.Core;
using Intersect.Enums;
using Intersect.Framework.Core;
using Intersect.Framework.Core.GameObjects.Animations;
using Intersect.Framework.Core.GameObjects.Items;
using Intersect.Framework.Core.GameObjects.Maps;
using Intersect.Framework.Core.GameObjects.Maps.Attributes;
using Intersect.Framework.Core.GameObjects.PlayerClass;
using Intersect.GameObjects;
using Intersect.Network.Packets.Server;
using Intersect.Utilities;
using Microsoft.Extensions.Logging;

namespace Intersect.Client.Entities;

public partial class Entity : IEntity
{
    public int AnimationFrame { get; set; }

    private readonly List<Animation> _animations = [];
    private readonly Dictionary<AnimationSource, Animation> _animationsBySource = [];

    //Animation Timer (for animated sprites)
    public long AnimationTimer { get; set; }

    //Combat
    public long AttackTimer { get; set; } = 0;

    public int AttackTime { get; set; } = -1;

    public long CastTime { get; set; } = 0;

    //Combat Status
    public bool IsAttacking => AttackTimer > Timing.Global.Milliseconds;

    public bool IsBlocking { get; set; } = false;

    public bool IsCasting => CastTime > Timing.Global.Milliseconds;

    protected readonly bool EnableTurnAroundWhileCasting = Options.Instance.Combat.EnableTurnAroundWhileCasting;

    public bool CanTurnAround => !IsMoving && (!IsCasting || EnableTurnAroundWhileCasting);

    public bool IsDashing => Dashing != null;

    //Dashing instance
    public Dash? Dashing { get; set; }

    public IDash? CurrentDash => Dashing;

    public Queue<Dash> DashQueue { get; set; } = new Queue<Dash>();

    public long DashTimer { get; set; }

    public float elapsedtime { get; set; } //to be removed

    private Guid[] _equipment = new Guid[Options.Instance.Equipment.Slots.Count];

    public bool HasAnimations => _animations.Count > 0;

    public Guid[] Equipment
    {
        get => _equipment;
        set
        {
            if (_equipment == value)
            {
                return;
            }

            _equipment = value;
            LoadAnimationTexture(
                string.IsNullOrWhiteSpace(TransformedSprite) ? Sprite : TransformedSprite,
                SpriteAnimations.Weapon
            );
        }
    }

    IReadOnlyList<int> IEntity.EquipmentSlots => [..MyEquipment];

    public Animation?[] EquipmentAnimations { get; set; } = new Animation[Options.Instance.Equipment.Slots.Count];

    //Extras
    public string Face { get; set; } = string.Empty;

    public Label FooterLabel { get; set; } = new(string.Empty, Color.White);

    public Gender Gender { get; set; } = Gender.Male;

    public Label HeaderLabel { get; set; } = new(string.Empty, Color.White);

    public bool IsHidden { get; set; } = false;

    public bool HideName { get; set; }

    //Core Values
    public Guid Id { get; set; }

    //Inventory/Spells/Equipment
    public IItem[] Inventory { get; } = new IItem[Options.Instance.Player.MaxInventory];

    IReadOnlyList<IItem> IEntity.Items => [.. Inventory];

    public bool InView { get; set; } = true;

    /// <summary>
    /// Indicates whether this entity is currently in motion.
    /// Used to control offset interpolation and animation state.
    /// </summary>
    public bool IsMoving { get; set; }

    //Caching
    public IMapInstance? LatestMap { get; set; }

    public int Level { get; set; } = 1;

    //Vitals & Stats
    public long[] MaxVital { get; set; } = new long[Enum.GetValues<Vital>().Length];

    IReadOnlyDictionary<Vital, long> IEntity.MaxVitals =>
        Enum.GetValues<Vital>().ToDictionary(vital => vital, vital => MaxVital[(int)vital]);

    protected Vector2 mOrigin = Vector2.Zero;

    //Chat
    private readonly List<ChatBubble> mChatBubbles = [];

    private Direction _directionFacing;

    protected bool mDisposed;

    private long mLastUpdate;

    protected string _sprite = string.Empty;

    public Color Color { get; set; } = new Color(255, 255, 255, 255);

    public virtual Direction DirectionMoving { get; set; } = Direction.None;

    public long MoveTimer { get; set; }

    protected byte mRenderPriority = 1;

    protected string mTransformedSprite = string.Empty;

    private long mWalkTimer;

    public int[] MyEquipment { get; set; } = new int[Options.Instance.Equipment.Slots.Count];

    public string Name { get; set; } = string.Empty;

    public Color? NameColor { get; set; } = null;

    public bool Passable { get; set; }

    //Rendering Variables
    public HashSet<Entity>? RenderList { get; set; }

    private Guid _spellCast;

    public Guid SpellCast
    {
        get => _spellCast;
        set
        {
            if (value == SpellCast)
            {
                return;
            }

            _spellCast = value;
            LoadAnimationTexture(string.IsNullOrWhiteSpace(TransformedSprite) ? Sprite : TransformedSprite, SpriteAnimations.Cast);
        }
    }

    public Spell[] Spells { get; set; } = new Spell[Options.Instance.Player.MaxSpells];

    IReadOnlyList<Guid> IEntity.Spells => Spells.Select(x => x.Id).ToList();

    public int[] Stat { get; set; } = new int[Enum.GetValues<Stat>().Length];

    IReadOnlyDictionary<Stat, int> IEntity.Stats =>
        Enum.GetValues<Stat>().ToDictionary(stat => stat, stat => Stat[(int)stat]);

    public IGameTexture? Texture { get; set; }

    #region "Animation Textures and Timing"

    public SpriteAnimations SpriteAnimation { get; set; } = SpriteAnimations.Normal;

    public Dictionary<SpriteAnimations, IGameTexture> AnimatedTextures { get; set; } = [];

    public int SpriteFrame { get; set; } = 0;

    public long SpriteFrameTimer { get; set; } = -1;

    public long LastActionTime { get; set; } = -1;

    public long AutoTurnToTargetTimer { get; set; } = -1;

    #endregion

    public EntityType Type { get; }

    public NpcAggression Aggression { get; set; }

    public long[] Vital { get; set; } = new long[Enum.GetValues<Vital>().Length];

    IReadOnlyDictionary<Vital, long> IEntity.Vitals =>
        Enum.GetValues<Vital>().ToDictionary(vital => vital, vital => Vital[(int)vital]);

    public int WalkFrame { get; set; }

    public FloatRect WorldPos { get; set; } = new FloatRect();

    public bool IsHovered { get; set; }

    private Vector3 _position;

    public Vector3 Position
    {
        get => _position;
        set
        {
            if (_position == value)
            {
                return;
            }

            var oldValue = _position;
            var delta = value - oldValue;
            _position = value;
            OnPositionChanged(value, oldValue);
        }
    }

    protected virtual void OnPositionChanged(Vector3 newPosition, Vector3 oldPosition) {}

    public int TileX => (int)float.Floor(Position.X);
    public int TileY => (int)float.Floor(Position.Y);
    public int TileZ => (int)float.Floor(Position.Z);

    //Location Info
    public byte X
    {
        get => (byte)float.Floor(Position.X);
        set => Position = Position with { X = value };
    }

    public byte Y
    {
        get => (byte)float.Floor(Position.Y);
        set => Position = Position with { Y = value };
    }

    public byte Z
    {
        get => (byte)float.Floor(Position.Z);
        set => Position = Position with { Z = value };
    }

    /// <summary>
    /// The horizontal pixel offset from the entity's tile position.
    /// Used for smooth movement interpolation between tiles.
    /// Ranges from -TileWidth to +TileWidth during movement.
    /// </summary>
    public float OffsetX { get; set; }

    /// <summary>
    /// The vertical pixel offset from the entity's tile position.
    /// Used for smooth movement interpolation between tiles.
    /// Ranges from -TileHeight to +TileHeight during movement.
    /// </summary>
    public float OffsetY { get; set; }

    public Entity(Guid id, EntityPacket? packet, EntityType entityType)
    {
        Id = id;
        Type = entityType;
        MapId = Guid.Empty;

        if (Id != Guid.Empty && Type != EntityType.Event Type)
        {
            for (var i = 0; i < Options.Instance.Player.MaxInventory; i++)
            {
                Inventory[i] = new Item();
            }

            for (var i = 0; i < Options.Instance.Player.MaxSpells; i++)
            {
                Spells[i] = new Spell();
            }

            for (var i = 0; i < Options.Instance.Equipment.Slots.Count; i++)
            {
                Equipment[i] = Guid.Empty;
                MyEquipment[i] = -1;
            }
        }

        AnimationTimer = Timing.Global.MillisecondsUtc + Globals.Random.Next(0, 500);

        //TODO Remove because fixed orrrrr change the exception text
        if (Options.Instance.Equipment.Slots.Count == 0)
        {
            throw new Exception("What the fuck is going on!?!?!?!?!?!");
        }

        Load(packet);
    }

    //Status effects
    public List<IStatus> Status { get; private set; } = [];

    IReadOnlyList<IStatus> IEntity.Status => Status;

    public Vector2 Origin => LatestMap == default ? Vector2.Zero : mOrigin;

    protected virtual Vector2 CenterOffset => (Texture == default) ? Vector2.Zero : (Vector2.UnitY * Texture.Center.Y / Options.Instance.Sprites.Directions);

    public Vector2 Center => Origin - CenterOffset;

    public Direction DirectionFacing
    {
        get => _directionFacing;
        set => _directionFacing = (Direction)((int)(value + Options.Instance.Map.MovementDirections) % Options.Instance.Map.MovementDirections);
    }

    private Direction _lastDirection = Direction.Down;

    public virtual string TransformedSprite
    {
        get => mTransformedSprite;
        set
        {
            if (mTransformedSprite == value)
            {
                return;
            }

            mTransformedSprite = value;

            var textureName = string.IsNullOrEmpty(mTransformedSprite) ? _sprite : mTransformedSprite;
            LoadTextures(textureName);
        }
    }

    public virtual string Sprite
    {
        get => _sprite;
        set
        {
            if (_sprite == value)
            {
                return;
            }

            _sprite = value;
            LoadTextures(_sprite);
        }
    }

    public virtual int SpriteFrames
    {
        get
        {
            return SpriteAnimation switch
            {
                SpriteAnimations.Normal => Options.Instance.Sprites.NormalFrames,
                SpriteAnimations.Idle => Options.Instance.Sprites.IdleFrames,
                SpriteAnimations.Attack => Options.Instance.Sprites.AttackFrames,
                SpriteAnimations.Shoot => Options.Instance.Sprites.ShootFrames,
                SpriteAnimations.Cast => Options.Instance.Sprites.CastFrames,
                SpriteAnimations.Weapon => Options.Instance.Sprites.WeaponFrames,
                _ => Options.Instance.Sprites.NormalFrames,
            };
        }
    }

    public IMapInstance? MapInstance => Maps.MapInstance.Get(MapId);

    public virtual Guid MapId { get; set; }

    //Deserializing
    public virtual void Load(EntityPacket? packet)
    {
        if (packet == null)
        {
            return;
        }

        MapId = packet.MapId;
        Name = packet.Name;
        Sprite = packet.Sprite;
        Color = packet.Color;
        Face = packet.Face;
        Level = packet.Level;
        Position = packet.Position;
        DirectionFacing = (Direction)packet.Dir;
        Passable = packet.Passable;
        HideName = packet.HideName;
        IsHidden = packet.HideEntity;
        NameColor = packet.NameColor;
        HeaderLabel = new Label(packet.HeaderLabel.Label, packet.HeaderLabel.Color);
        FooterLabel = new Label(packet.FooterLabel.Label, packet.FooterLabel.Color);

        var animsToClear = new List<Animation>();
        var animsToAdd = new List<AnimationDescriptor>();
        for (var i = 0; i < packet.Animations.Length; i++)
        {
            var anim = AnimationDescriptor.Get(packet.Animations[i]);
            if (anim != null)
            {
                animsToAdd.Add(anim);
            }
        }

        foreach (var anim in _animations)
        {
            animsToClear.Add(anim);
            if (!anim.InfiniteLoop)
            {
                _ = animsToClear.Remove(anim);
            }
            else
            {
                foreach (var addedAnim in animsToAdd)
                {
                    if (addedAnim.Id == anim?.Descriptor?.Id)
                    {
                        _ = animsToClear.Remove(anim);
                        _ = animsToAdd.Remove(addedAnim);

                        break;
                    }
                }

                foreach (var equipAnim in EquipmentAnimations)
                {
                    if (equipAnim == anim && anim != null)
                    {
                        _ = animsToClear.Remove(anim);
                    }
                }
            }
        }

        RemoveAnimations(animsToClear);
        AddAnimations(animsToAdd);

        Vital = packet.Vital;
        MaxVital = packet.MaxVital;

        //Update status effects
        Status.Clear();

        if (packet.StatusEffects == null)
        {
            ApplicationContext.Context.Value?.Logger.LogWarning($"'{nameof(packet)}.{nameof(packet.StatusEffects)}' is null.");
        }
        else
        {
            foreach (var status in packet.StatusEffects)
            {
                var instance = new Status(
                    status.SpellId, status.Type, status.TransformSprite, status.TimeRemaining, status.TotalDuration
                );

                Status?.Add(instance);

                if (instance.Type == SpellEffect.Shield)
                {
                    instance.Shield = status.VitalShields;
                }
            }
        }

        SortStatuses();
        Stat = packet.Stats;

        mDisposed = false;

        //Status effects box update
        if (Globals.Me == null)
        {
            ApplicationContext.Context.Value?.Logger.LogWarning($"'{nameof(Globals.Me)}' is null.");
        }
        else
        {
            if (Id == Globals.Me.Id)
            {
                Interface.Interface.EnqueueInGame(
                    gameInterface =>
                    {
                        if (gameInterface.PlayerStatusWindow == null)
                        {
                            ApplicationContext.Context.Value?.Logger.LogWarning(
                                $"'{nameof(gameInterface.PlayerStatusWindow)}' is null."
                            );
                        }
                        else
                        {
                            gameInterface.PlayerStatusWindow.ShouldUpdateStatuses = true;
                        }
                    },
                    (entityId, entityName) => ApplicationContext.CurrentContext.Logger.LogWarning(
                        "Tried to load entity {EntityId} ({EntityName}) from packet before in-game UI was ready",
                        entityId,
                        entityName
                    ),
                    packet.EntityId,
                    packet.Name
                );
            }
            else if (Id != Guid.Empty && Id == Globals.Me.TargetId)
            {
                if (Globals.Me.TargetBox == null)
                {
                    ApplicationContext.Context.Value?.Logger.LogWarning($"'{nameof(Globals.Me.TargetBox)}' is null.");
                }
                else
                {
                    Globals.Me.TargetBox.ShouldUpdateStatuses = true;
                }
            }
        }
    }

    public void AddAnimations(List<AnimationDescriptor> animationDescriptors)
    {
        foreach (var animationDescriptor in animationDescriptors)
        {
            TryAddAnimation(new Animation(animationDescriptor, true, false, -1, this));
        }
    }

    public virtual bool IsAllyOf(Player en)
    {
        if (en == null || MapInstance == default || en.MapInstance == default)
        {
            return false;
        }

        // Resources have no allies
        if (Type == EntityType.Resource)
        {
            return false;
        }

        // Events ONLY have allies
        if (Type == EntityType.Event)
        {
            return true;
        }

        // Yourself is always an ally
        if (en.Id == Id)
        {
            return true;
        }

        return false;
    }

    public void ClearAnimations() => RemoveAnimations(_animations);

    public void RemoveAnimations(IEnumerable<Animation> animations, bool dispose = true)
    {
        var animationsToRemove = animations.ToArray();
        foreach (var animation in animationsToRemove)
        {
            if (dispose)
            {
                if (!animation.IsDisposed)
                {
                    animation.Dispose();
                }
            }

            _ = TryRemoveAnimation(animation);
        }
    }

    public bool IsDisposed => mDisposed;

    public virtual void Dispose()
    {
        if (RenderList != null)
        {
            _ = RenderList.Remove(this);
        }

        ClearAnimations();
        GC.SuppressFinalize(this);
        mDisposed = true;
    }

    /// <summary>
    /// Calculates the time (in milliseconds) required for this entity to traverse one tile.
    /// 
    /// The base calculation is: 1000ms / (1 + ln(Speed))
    /// - Higher speed stat = faster movement (less time per tile)
    /// - Logarithmic scaling prevents excessive speed at high stat values
    /// 
    /// Diagonal movement adjustment:
    /// - Multiplied by √2 (≈1.414) for diagonal directions to maintain consistent visual speed
    /// - This compensates for the longer diagonal distance (Pythagorean theorem: √(1² + 1²) = √2)
    /// 
    /// Blocking penalty:
    /// - Additional slowdown based on Options.Combat.BlockingSlow when blocking
    /// </summary>
    /// <returns>Movement time in milliseconds, capped at 1000ms maximum</returns>
    public virtual float GetMovementTime()
    {
        var time = 1000f / (float)(1 + Math.Log(Stat[(int)Enums.Stat.Speed]));
        
        // Adjust for diagonal movement - compensate for longer distance
        if (DirectionFacing > Direction.Right)
        {
            time *= MathHelper.UnitDiagonalLength;
        }

        // Apply blocking slowdown penalty
        if (IsBlocking)
        {
            time += time * Options.Instance.Combat.BlockingSlow;
        }

        return Math.Min(1000f, time);
    }

    public override string ToString() => Name;

    #region Movement Processing

    /// <summary>
    /// Main update loop for entity state, animation, and movement processing.
    /// Called once per frame to update:
    /// - Movement interpolation and offsets
    /// - Animation states and frames
    /// - Equipment animations
    /// - Chat bubbles
    /// - Render ordering
    /// </summary>
    public virtual bool Update()
    {
        if (mDisposed)
        {
            LatestMap = null;
            return false;
        }

        if (LatestMap?.Id != MapId)
        {
            LatestMap = Maps.MapInstance.Get(MapId);
        }

        if (LatestMap == null || !LatestMap.InView())
        {
            Globals.EntitiesToDispose.Add(Id);
            return false;
        }

        RenderList = DetermineRenderOrder(RenderList, LatestMap);
        if (mLastUpdate == 0)
        {
            mLastUpdate = Timing.Global.Milliseconds;
        }

        // Calculate elapsed time since last update (delta time)
        var ecTime = (float)(Timing.Global.Milliseconds - mLastUpdate);
        elapsedtime = ecTime;
        
        // Handle dashing state - fix walk frame during dash
        if (Dashing != null)
        {
            WalkFrame = Options.Instance.Sprites.NormalDashFrame;
        }
        // Update walk animation frames at regular intervals
        else if (mWalkTimer < Timing.Global.Milliseconds)
        {
            // Check for queued dash when not moving
            if (!IsMoving && DashQueue.Count > 0)
            {
                Dashing = DashQueue.Dequeue();
                Dashing.Start(this);
                OffsetX = 0;
                OffsetY = 0;
                DashTimer = Timing.Global.Milliseconds + Options.Instance.Combat.MaxDashSpeed;
            }
            else
            {
                // Advance walk frame during movement
                if (IsMoving)
                {
                    WalkFrame++;
                    if (WalkFrame >= SpriteFrames)
                    {
                        WalkFrame = 0;
                    }
                }
                // Gradually return to idle stance when stopped
                else
                {
                    if (WalkFrame > 0 && WalkFrame / SpriteFrames < 0.7f)
                    {
                        WalkFrame = SpriteFrames / 2;
                    }
                    else
                    {
                        WalkFrame = 0;
                    }
                }

                mWalkTimer = Timing.Global.Milliseconds + Options.Instance.Sprites.MovingFrameDuration;
            }
        }

        // Process dash movement
        if (Dashing != null)
        {
            if (Dashing.Update(this))
            {
                OffsetX = Dashing.GetXOffset();
                OffsetY = Dashing.GetYOffset();
            }
            else
            {
                OffsetX = 0;
                OffsetY = 0;
            }
        }
        // Process normal grid-based movement with offset interpolation
        else if (IsMoving)
        {
            // Calculate pixel displacement for this frame
            // Formula: (elapsed_ms * pixels_per_tile) / ms_per_tile
            var displacementTime = ecTime * TileHeight / GetMovementTime();

            PickLastDirection(DirectionFacing);

            // Update offsets based on movement direction
            // Perpendicular axis is reset to 0 to prevent drift
            // Movement continues until offset crosses the tile boundary (reaches 0)
            switch (DirectionFacing)
            {
                case Direction.Up:
                    OffsetY -= displacementTime;
                    OffsetX = 0; // Reset perpendicular axis
                    if (OffsetY < 0)
                    {
                        OffsetY = 0; // Snap to tile boundary
                    }
                    break;

                case Direction.Down:
                    OffsetY += displacementTime;
                    OffsetX = 0;
                    if (OffsetY > 0)
                    {
                        OffsetY = 0;
                    }
                    break;

                case Direction.Left:
                    OffsetX -= displacementTime;
                    OffsetY = 0;
                    if (OffsetX < 0)
                    {
                        OffsetX = 0;
                    }
                    break;

                case Direction.Right:
                    OffsetX += displacementTime;
                    OffsetY = 0;
                    if (OffsetX > 0)
                    {
                        OffsetX = 0;
                    }
                    break;
                    
                // Diagonal directions - update both axes simultaneously
                case Direction.UpLeft:
                    OffsetY -= displacementTime;
                    OffsetX -= displacementTime;
                    if (OffsetY < 0)
                    {
                        OffsetY = 0;
                    }
                    if (OffsetX < 0)
                    {
                        OffsetX = 0;
                    }
                    break;
                    
                case Direction.UpRight:
                    OffsetY -= displacementTime;
                    OffsetX += displacementTime;
                    if (OffsetY < 0)
                    {
                        OffsetY = 0;
                    }
                    if (OffsetX > 0)
                    {
                        OffsetX = 0;
                    }
                    break;
                    
                case Direction.DownLeft:
                    OffsetY += displacementTime;
                    OffsetX -= displacementTime;
                    if (OffsetY > 0)
                    {
                        OffsetY = 0;
                    }
                    if (OffsetX < 0)
                    {
                        OffsetX = 0;
                    }
                    break;
                    
                case Direction.DownRight:
                    OffsetY += displacementTime;
                    OffsetX += displacementTime;
                    if (OffsetY > 0)
                    {
                        OffsetY = 0;
                    }
                    if (OffsetX > 0)
                    {
                        OffsetX = 0;
                    }
                    break;
            }

            // Movement complete when both offsets reach tile boundaries (0)
            if (OffsetX == 0 && OffsetY == 0)
            {
                IsMoving = false;
            }
        }

        //Check to see if we should start or stop equipment animations
        if (Equipment.Length == Options.Instance.Equipment.Slots.Count)
        {
            for (var z = 0; z < Options.Instance.Equipment.Slots.Count; z++)
            {
                var equipmentAnimation = EquipmentAnimations[z];
                if (Equipment[z] != Guid.Empty && (this != Globals.Me || MyEquipment[z] < Options.Instance.Player.MaxInventory))
                {
                    var itemId = (this == Globals.Me && MyEquipment[z] > -1)
                        ? Inventory[MyEquipment[z]].ItemId
                        : Equipment[z];

                    if (ItemDescriptor.TryGet(itemId, out var itemDescriptor) && itemDescriptor.EquipmentAnimation is { } animationDescriptor)
                    {
                        if (equipmentAnimation == null || equipmentAnimation.Descriptor != animationDescriptor || equipmentAnimation.IsDisposed)
                        {
                            if (equipmentAnimation != null)
                            {
                                TryRemoveAnimation(equipmentAnimation, dispose: true);
                            }

                            var newAnimation = new Animation(animationDescriptor, true, true, -1, this);
                            EquipmentAnimations[z] = newAnimation;
                            _animations.Add(newAnimation);
                        }
                    }
                    else if (equipmentAnimation != null)
                    {
                        TryRemoveAnimation(equipmentAnimation, dispose: true);
                        EquipmentAnimations[z] = null;
                    }
                }
                else if (equipmentAnimation != null)
                {
                    TryRemoveAnimation(equipmentAnimation, dispose: true);
                    EquipmentAnimations[z] = null;
                }
            }
        }

        var chatbubbles = mChatBubbles.ToArray();
        foreach (var chatbubble in chatbubbles)
        {
            if (!chatbubble.Update())
            {
                _ = mChatBubbles.Remove(chatbubble);
            }
        }

        if (AnimationTimer < Timing.Global.MillisecondsUtc)
        {
            AnimationTimer = Timing.Global.MillisecondsUtc + 200;
            AnimationFrame++;
            if (AnimationFrame >= SpriteFrames)
            {
                AnimationFrame = 0;
            }
        }

        CalculateOrigin();

        List<Animation>? disposedAnimations = null;
        foreach (var animation in _animations)
        {
            animation.Update();

            //If disposed mark to be removed and continue onward
            if (animation.IsDisposed)
            {
                disposedAnimations ??= [];
                disposedAnimations.Add(animation);
                continue;
            }

            if (IsStealthed || IsHidden)
            {
                animation.Hide();
            }
            else
            {
                animation.Show();
            }

            var animationDirection = animation.AutoRotate ? DirectionFacing : default;
            animation.SetPosition(
                (int)Math.Ceiling(Center.X),
                (int)Math.Ceiling(Center.Y),
                X,
                Y,
                MapId,
                animationDirection, Z
            );
        }

        if (disposedAnimations != null)
        {
            RemoveAnimations(disposedAnimations);
        }

        mLastUpdate = Timing.Global.Milliseconds;

        UpdateSpriteAnimation();

        return true;
    }

    #endregion