namespace Intersect.Server.Entities
{

    public partial class Entity
    {
        private bool OnGround()
        {
            bool result = false;

            Entity blockedBy = null;

            // This means that the entity is on the ground as we know it.
            if (CanMove(1) == -2 || CanMove(1) == -7 || CanMove(1) == -8)
            {
                result = true;
            }

            return result;
        }
        
         private void PlatformingChecksA()
        {
            // Is the player on the ground?
            if (OnGround())
            {
                this.Jumping = false;
                this.IsJumping = false;
                this.Falling = false;
            }

            // Should the player fall?
            //if (!OnGround() && this.Jumping == false && this.Climbing == false)
            if (!OnGround() && this.Jumping == false && this.Climbing == false)
            {
                this.Falling = true;
                this.IsJumping = true;
            }
            else
            {
                this.Falling = false;
                this.FallDir = -1;
                this.IsJumping = false;
            }

            // this.Falling.
            if (this.Falling)
            {
                this.Jumping = false;
                if (!OnGround())
                {
                    if (this.Dir == 2)
                    {
                        this.FallDir = 6;
                    }

                    if (this.Dir == 3)
                    {
                        this.FallDir = 7;
                    }

                    if (this.Dir == 1 && this.JumpDir == -1)
                    {
                        this.FallDir = 1;
                    }

                    switch (this.FallDir)
                    {
                        case 1:
                            this.Dir = 1;
                            break;
                        case 6:
                            this.Dir = 6;
                            break;
                        case 7:
                            this.Dir = 7;
                            break;
                    }
                }
            }

            // this.Climbing.
            if (this.Climbing)
            {
                this.IsJumping = false;
                this.Jumping = false;
                this.Falling = false;
                this.JumpDir = -1;
            }

            // Try to jump.
            if ((Dir == 0 || Dir == 4 || Dir == 5) && Jumping == false && this.Falling == false && OnGround())
            {
                this.IsJumping = true;
                this.Jumping = true;
                this.FallDir = -1;
            }

            // Jump (up to a certain height).
            if (Jumping)
            {
                if (this.JumpHeight >= Options.JumpHeight)
                {
                    this.Jumping = false;
                    this.JumpHeight = 0;

                    if (OnGround() == false)
                    {
                        switch (this.JumpDir)
                        {
                            case 0:
                                this.Dir = 1;
                                this.FallDir = 1;
                                break;
                            case 4:
                                Dir = 6;
                                this.FallDir = 6;
                                break;
                            case 5:
                                this.Dir = 7;
                                this.FallDir = 7;
                                break;
                        }
                    }

                    this.JumpDir = -1;
                }
                // Jumping.
                else
                {
                    this.IsJumping = true;
                    if (Dir == 2)
                    {
                        this.JumpDir = 4;
                    }

                    if (Dir == 3)
                    {
                        this.JumpDir = 5;
                    }

                    if (Dir == 1 && this.JumpDir == -1)
                    {
                        this.JumpDir = 0;
                    }

                    switch (this.JumpDir)
                    {
                        case 0:
                            this.Dir = 0;
                            break;
                        case 4:
                            this.Dir = 4;
                            break;
                        case 5:
                            this.Dir = 5;
                            break;
                    }
                }
            }
        }

        private void EdgeBlockOnGround()
        {
            this.Climbing = false;
            this.IsJumping = true;
            this.FallDir = 1;
            this.JumpDir = -1;
        }

        private void BlockTileOnAir()
        {
            //OffsetY = 0;
            //OffsetX = 0;
            this.FallDir = -1;
            this.JumpHeight = 0;
            this.Dir = 1;
            this.IsJumping = false;
            this.Jumping = false;
            if (OnGround())
            {
                return;
            }

            this.JumpDir = -1;
            //OffsetX = 0;
            //OffsetY = -Options.TileHeight;
            this.FallDir = 1;
            this.JumpHeight = 0;
            this.Dir = 1;
            this.IsJumping = false;
            //IsMoving = true;
            this.Falling = true;
            this.Climbing = false;
        }
    }

}
