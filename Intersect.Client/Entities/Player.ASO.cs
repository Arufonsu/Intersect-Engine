using Intersect.Client.Core.Controls;
using Intersect.Client.Framework.Entities;

namespace Intersect.Client.Entities
{
    public partial class Player
    {
        private void PlatformingChecksA()
        {
            // Is the player on the ground?
            if (OnGround())
            {
                Jumping = false;
                IsJumping = false;
                Falling = false;
            }

            // Should the player fall?
            if (!OnGround() && Jumping == false && Climbing == false)
            {
                Falling = true;
                IsJumping = true;
            }
            else
            {
                Falling = false;
                FallDir = -1;
                IsJumping = false;
            }

            // Falling.
            if (Falling)
            {
                Jumping = false;
                if (!OnGround())
                {
                    if (Controls.KeyDown(Control.MoveLeft))
                    {
                        FallDir = 6;
                    }

                    if (Controls.KeyDown(Control.MoveRight))
                    {
                        FallDir = 7;
                    }

                    if (Controls.KeyDown(Control.MoveDown) && JumpDir == -1)
                    {
                        FallDir = 1;
                    }

                    switch (FallDir)
                    {
                        case 1:
                            MoveDir = 1;
                            break;
                        case 6:
                            MoveDir = 6;
                            break;
                        case 7:
                            MoveDir = 7;
                            break;
                    }
                }
            }

            // Climbing.
            if (Climbing)
            {
                IsJumping = false;
                Jumping = false;
                Falling = false;
                JumpDir = -1;
            }

            // Try to jump.
            if ((MoveDir == 0 || MoveDir == 4 || MoveDir == 5) && Jumping == false && Falling == false && OnGround())
            {
                IsJumping = true;
                Jumping = true;
                FallDir = -1;
            }

            // Jump (up to a certain height).
            if (Jumping)
            {
                if (JumpHeight >= Options.JumpHeight)
                {
                    Jumping = false;
                    JumpHeight = 0;

                    if (OnGround() == false)
                    {
                        switch (JumpDir)
                        {
                            case 0:
                                MoveDir = 1;
                                FallDir = 1;
                                break;
                            case 4:
                                MoveDir = 6;
                                FallDir = 6;
                                break;
                            case 5:
                                MoveDir = 7;
                                FallDir = 7;
                                break;
                        }
                    }

                    JumpDir = -1;
                }
                // Jumping.
                else
                {
                    IsJumping = true;
                    if (Controls.KeyDown(Control.MoveLeft))
                    {
                        JumpDir = 4;
                    }

                    if (Controls.KeyDown(Control.MoveRight))
                    {
                        JumpDir = 5;
                    }

                    if (Controls.KeyDown(Control.MoveDown) && JumpDir == -1)
                    {
                        JumpDir = 0;
                    }

                    switch (JumpDir)
                    {
                        case 0:
                            MoveDir = 0;
                            break;
                        case 4:
                            MoveDir = 4;
                            break;
                        case 5:
                            MoveDir = 5;
                            break;
                    }
                }
            }
        }

        private void EdgeBlockOnGround()
        {
            Climbing = false;
            IsJumping = true;
            FallDir = 1;
            JumpDir = -1;
        }

        private void BlockTileOnGround()
        {
            IsMoving = false;
            OffsetY = 0;
            OffsetX = 0;
        }

        private void BlockTileOnAir(ref sbyte tmpY)
        {
            OffsetY = 0;
            OffsetX = 0;
            FallDir = -1;
            JumpHeight = 0;
            Dir = 1;
            IsJumping = false;
            Jumping = false;
            if (OnGround())
            {
                return;
            }
            JumpDir = -1;
            OffsetX = 0;
            OffsetY = -Options.TileHeight;
            FallDir = 1;
            JumpHeight = 0;
            Dir = 1;
            IsJumping = false;
            IsMoving = true;
            Falling = true;
            Climbing = false;
            tmpY++;
        }
    }
}
