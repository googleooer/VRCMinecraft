using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using Nessie.Udon.Movement;
using VRRefAssist;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McMovement : NUMovement
{
    private const float MC_TICK = 0.05f;
    private const float GRAVITY_PER_TICK = 0.08f;
    private const float WATER_DRAG = 0.8f;
    private const float LAVA_DRAG = 0.5f;
    private const float AIR_DRAG = 0.91f;
    private const float MOVE_BASE = 0.1f;
    private const float MOVE_AIR = 0.02f;
    private const float WATER_ACCEL = 0.02f;
    private const float JUMP_MOTION = 0.42f;
    private const float LADDER_MAX_SPEED = 0.15f;
    private const float LADDER_PUSH_UP = 0.2f;
    private const float WATER_JUMP_BOOST = 0.04f;
    private const float WEB_HORIZONTAL_SCALE = 0.25f;
    private const float WEB_VERTICAL_SCALE = 0.05f;
    private const float SOUL_SAND_MULTIPLIER = 0.4f;
    private const float DEFAULT_SLIPPERINESS = 0.6f;
    private const float INPUT_DAMP = 0.98f;
    private const float VERTICAL_DAMP = 0.98f;

    [Header("World References")]
    public McWorld world;
    [SerializeField, FindObjectOfType(true)] private McParticleManager particleManager;
    public bool flipXAxis = false;

    private Vector3 mcVelocity; // Minecraft displacement per tick.
    private Vector3 lastHorizontalInput;
    private bool wasOnGround;
    private float fallDistance;
    private byte blockBelowId;
    private bool inWater;
    private bool inLava;
    private bool onLadder;
    private bool inWeb;
    private float groundSlipperiness = DEFAULT_SLIPPERINESS;
    private bool hasEnvironmentStateSample;

    protected override void ControllerStart()
    {
        base.ControllerStart();

        scaleMovement = false;
        groundSnap = false;
        jumpCancel = false;

        Controller.skinWidth = 0.001f;
        Controller.height = 1.8f;
        Controller.radius = 0.3f;
        Controller.center = Vector3.up * 0.9f;

        _SetWalkSpeed(0f);
        _SetRunSpeed(0f);
        _SetStrafeSpeed(0f);
        _SetJumpHeight(0f);
        _SetGravityStrength(0f);

        mcVelocity = Vector3.zero;
        Velocity = Vector3.zero;
        hasEnvironmentStateSample = false;
    }

    protected override void ControllerUpdate()
    {
        if (!isActive)
        {
            return;
        }

        ApplyGround();
        bool wasInWater = inWater;
        UpdateEnvironmentState();
        if (hasEnvironmentStateSample && inWater && !wasInWater)
        {
            EmitWaterEntryParticles();
        }
        hasEnvironmentStateSample = true;

        float tickRatio = DeltaTime > 0f ? DeltaTime / MC_TICK : 0f;
        if (tickRatio <= 0f)
        {
            ApplyGroundSnap();
            ApplyToPlayer();
            return;
        }

        Vector2 input = GetInput();
        bool jumpPressed = HoldJump;
        bool sneaking = DetectSneak();

        float strafe = input.x * INPUT_DAMP;
        float forward = input.y * INPUT_DAMP;
        if (sneaking)
        {
            strafe *= 0.3f;
            forward *= 0.3f;
        }

        if (inWater)
        {
            SimulateFluid(strafe, forward, tickRatio, jumpPressed, WATER_ACCEL);
        }
        else if (inLava)
        {
            SimulateFluid(strafe, forward, tickRatio, jumpPressed, WATER_ACCEL);
        }
        else
        {
            SimulateGroundAir(strafe, forward, tickRatio, jumpPressed, sneaking);
        }

        Vector3 physicalDisplacement = VelocityFromTick(mcVelocity) * DeltaTime;
        Vector3 motion = physicalDisplacement + MotionOffset;

        if (inWeb)
        {
            physicalDisplacement.x *= WEB_HORIZONTAL_SCALE;
            physicalDisplacement.z *= WEB_HORIZONTAL_SCALE;
            physicalDisplacement.y *= WEB_VERTICAL_SCALE;

            motion = physicalDisplacement + MotionOffset;
        }

        if (DeltaTime > 0f)
        {
            Velocity = physicalDisplacement / DeltaTime;
        }
        else
        {
            Velocity = Vector3.zero;
        }

        Move(motion);

        mcVelocity = Velocity * MC_TICK;
        if (inWeb)
        {
            mcVelocity = Vector3.zero;
        }

        ApplyPostMoveForces(tickRatio, sneaking);

        UpdateFallDistance(tickRatio);

        ApplyGroundSnap();
        ApplyToPlayer();

        wasOnGround = IsWalkable;
    }

    private Vector2 GetInput()
    {
        return new Vector2(-Mathf.Clamp(InputMoveX, -1f, 1f), Mathf.Clamp(InputMoveY, -1f, 1f));
    }

    private bool DetectSneak()
    {
        bool keyboardSneak = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool stanceSneak = PlayerStance == Stance.Crouching || PlayerStance == Stance.Prone;
        return keyboardSneak || stanceSneak;
    }

    private void SimulateFluid(float strafe, float forward, float tickRatio, bool jumpPressed, float accel)
    {
        ApplyMoveFlying(strafe, forward, accel, tickRatio);

        if (jumpPressed)
        {
            mcVelocity.y += WATER_JUMP_BOOST * tickRatio;
        }
    }

    private void SimulateGroundAir(float strafe, float forward, float tickRatio, bool jumpPressed, bool sneaking)
    {
        bool onGroundNow = IsWalkable;
        float slipperinessPre = onGroundNow ? groundSlipperiness * AIR_DRAG : AIR_DRAG;
        float accel = onGroundNow
            ? MOVE_BASE * (0.16277136f / (slipperinessPre * slipperinessPre * slipperinessPre))
            : MOVE_AIR;

        ApplyMoveFlying(strafe, forward, accel, tickRatio);

        if (onLadder)
        {
            mcVelocity.x = Mathf.Clamp(mcVelocity.x, -LADDER_MAX_SPEED, LADDER_MAX_SPEED);
            mcVelocity.z = Mathf.Clamp(mcVelocity.z, -LADDER_MAX_SPEED, LADDER_MAX_SPEED);
            mcVelocity.y = Mathf.Max(mcVelocity.y, -LADDER_MAX_SPEED);
            if (sneaking && mcVelocity.y < 0f)
            {
                mcVelocity.y = 0f;
            }
        }
        else if (jumpPressed && onGroundNow)
        {
            mcVelocity.y = JUMP_MOTION;
        }
    }

    private void ApplyPostMoveForces(float tickRatio, bool sneaking)
    {
        if (onLadder)
        {
            if ((Controller.collisionFlags & CollisionFlags.Sides) != 0)
            {
                mcVelocity.y = LADDER_PUSH_UP;
            }
            fallDistance = 0f;
        }

        if (inWater)
        {
            float drag = Mathf.Pow(WATER_DRAG, tickRatio);
            mcVelocity.x *= drag;
            mcVelocity.y *= drag;
            mcVelocity.z *= drag;
            mcVelocity.y -= 0.02f * tickRatio;
            if ((Controller.collisionFlags & CollisionFlags.Sides) != 0 && ShouldBoostUpwardsInFluid(true))
            {
                mcVelocity.y = 0.3f;
            }
            return;
        }

        if (inLava)
        {
            float drag = Mathf.Pow(LAVA_DRAG, tickRatio);
            mcVelocity.x *= drag;
            mcVelocity.y *= drag;
            mcVelocity.z *= drag;
            mcVelocity.y -= 0.02f * tickRatio;
            if ((Controller.collisionFlags & CollisionFlags.Sides) != 0 && ShouldBoostUpwardsInFluid(false))
            {
                mcVelocity.y = 0.3f;
            }
            return;
        }

        float friction = AIR_DRAG;
        if (IsWalkable)
        {
            float slipperiness = GetGroundSlipperiness();
            friction = slipperiness * AIR_DRAG;
            if (blockBelowId == (byte)BlockMaterial.SOUL_SAND)
            {
                float soul = Mathf.Pow(SOUL_SAND_MULTIPLIER, tickRatio);
                mcVelocity.x *= soul;
                mcVelocity.z *= soul;
            }
        }

        mcVelocity.y -= GRAVITY_PER_TICK * tickRatio;
        mcVelocity.y *= Mathf.Pow(VERTICAL_DAMP, tickRatio);
        mcVelocity.x *= Mathf.Pow(friction, tickRatio);
        mcVelocity.z *= Mathf.Pow(friction, tickRatio);
    }

    private void UpdateFallDistance(float tickRatio)
    {
        if (inWater || inLava)
        {
            fallDistance = 0f;
            return;
        }

        if (IsWalkable)
        {
            fallDistance = 0f;
        }
        else if (mcVelocity.y < 0f)
        {
            fallDistance += -mcVelocity.y * tickRatio;
        }
    }

    private void ApplyMoveFlying(float strafe, float forward, float accelPerTick, float tickRatio)
    {
        float magnitude = Mathf.Sqrt(strafe * strafe + forward * forward);
        if (magnitude < 1e-5f)
        {
            lastHorizontalInput = Vector3.zero;
            return;
        }

        if (magnitude < 1f)
        {
            float inv = 1f / magnitude;
            strafe *= inv;
            forward *= inv;
        }

        Vector3 forwardDir = Vector3.ProjectOnPlane(InputToWorld * Vector3.forward, ControllerUp).normalized;
        if (forwardDir.sqrMagnitude < 1e-5f)
        {
            forwardDir = Vector3.ProjectOnPlane(transform.forward, ControllerUp).normalized;
        }

        Vector3 rightDir = Vector3.Cross(ControllerUp, forwardDir).normalized;
        Vector3 leftDir = -rightDir;

        Vector3 inputDir = leftDir * strafe + forwardDir * forward;
        float inputMagnitude = inputDir.sqrMagnitude;
        if (inputMagnitude > 1e-5f)
        {
            Vector3 normalizedInput = inputDir.normalized;
            lastHorizontalInput = normalizedInput;
            mcVelocity += normalizedInput * (accelPerTick * tickRatio);
        }
        else
        {
            lastHorizontalInput = Vector3.zero;
        }
    }

    private void UpdateEnvironmentState()
    {
        inWater = false;
        inLava = false;
        onLadder = false;
        inWeb = false;
        blockBelowId = 0;
        groundSlipperiness = DEFAULT_SLIPPERINESS;

        if (world == null)
        {
            return;
        }

        Vector3 basePos = transform.position + Controller.center;
        float radius = Controller.radius;
        float halfHeight = Controller.height * 0.5f;
        float feetY = basePos.y - halfHeight - 0.01f;
        Vector3 footCenter = new Vector3(basePos.x, feetY, basePos.z);

        blockBelowId = SampleBlock(footCenter);
        groundSlipperiness = GetSlipperiness(blockBelowId);

        float[] sampleHeights = new float[]
        {
            basePos.y - halfHeight + 0.1f,
            basePos.y,
            basePos.y + halfHeight - 0.1f
        };

        for (int i = 0; i < sampleHeights.Length; i++)
        {
            float y = sampleHeights[i];
            Vector3 centerSample = new Vector3(basePos.x, y, basePos.z);
            InspectBlock(centerSample);
            InspectBlock(new Vector3(basePos.x + radius, y, basePos.z));
            InspectBlock(new Vector3(basePos.x - radius, y, basePos.z));
            InspectBlock(new Vector3(basePos.x, y, basePos.z + radius));
            InspectBlock(new Vector3(basePos.x, y, basePos.z - radius));
        }

        PushOutOfSolidBlock(basePos, halfHeight);
    }

    private void InspectBlock(Vector3 sample)
    {
        byte blockId = SampleBlock(sample);
        if (blockId == 0)
        {
            return;
        }

        if (blockId == (byte)BlockMaterial.WATER || blockId == (byte)BlockMaterial.STATIONARY_WATER)
        {
            inWater = true;
        }
        else if (blockId == (byte)BlockMaterial.LAVA || blockId == (byte)BlockMaterial.STATIONARY_LAVA)
        {
            inLava = true;
        }
        else if (blockId == (byte)BlockMaterial.LADDER)
        {
            onLadder = true;
        }
        else if (blockId == (byte)BlockMaterial.WEB)
        {
            inWeb = true;
        }
    }

    private void PushOutOfSolidBlock(Vector3 bodyCenter, float halfHeight)
    {
        float feetSampleY = bodyCenter.y - halfHeight + 0.1f;

        // If completely below the world, scan from a reasonable height to find ground
        if (transform.position.y < -1f)
        {
            _RecoverFromVoidFall(bodyCenter);
            return;
        }

        Vector3 feetSample = new Vector3(bodyCenter.x, feetSampleY, bodyCenter.z);
        Vector3 bodySample = bodyCenter;

        byte feetBlock = SampleBlock(feetSample);
        byte bodyBlock = SampleBlock(bodySample);

        if (!IsSolidBlock(feetBlock) && !IsSolidBlock(bodyBlock)) return;

        int baseVoxelY = Mathf.FloorToInt(feetSampleY);

        for (int dy = 1; dy <= 10; dy++)
        {
            Vector3 lowerProbe = new Vector3(bodyCenter.x, baseVoxelY + dy, bodyCenter.z);
            Vector3 upperProbe = new Vector3(bodyCenter.x, baseVoxelY + dy + 1, bodyCenter.z);
            if (!IsSolidBlock(SampleBlock(lowerProbe)) && !IsSolidBlock(SampleBlock(upperProbe)))
            {
                _TeleportToSafety(baseVoxelY + dy + 0.01f);
                return;
            }
        }
    }

    private void _RecoverFromVoidFall(Vector3 bodyCenter)
    {
        for (int y = 255; y >= 0; y--)
        {
            Vector3 probe = new Vector3(bodyCenter.x, y, bodyCenter.z);
            if (IsSolidBlock(SampleBlock(probe)))
            {
                Vector3 aboveA = new Vector3(bodyCenter.x, y + 1, bodyCenter.z);
                Vector3 aboveB = new Vector3(bodyCenter.x, y + 2, bodyCenter.z);
                if (!IsSolidBlock(SampleBlock(aboveA)) && !IsSolidBlock(SampleBlock(aboveB)))
                {
                    _TeleportToSafety(y + 1.01f);
                    return;
                }
            }
        }
    }

    private void _TeleportToSafety(float safeY)
    {
        Vector3 safePos = new Vector3(transform.position.x, safeY, transform.position.z);
        _SetPosition(safePos);
        mcVelocity = Vector3.zero;
        Velocity = Vector3.zero;
        fallDistance = 0f;
    }

    private byte SampleBlock(Vector3 worldPos)
    {
        if (world == null)
        {
            return 0;
        }

        int x = Mathf.FloorToInt(worldPos.x);
        int y = Mathf.FloorToInt(worldPos.y);
        int z = Mathf.FloorToInt(worldPos.z);

        if (flipXAxis)
        {
            x = -x;
        }

        return world.GetBlock(x, y, z);
    }

    private float GetGroundSlipperiness()
    {
        if (world == null)
        {
            return DEFAULT_SLIPPERINESS;
        }

        Vector3 basePos = transform.position + Controller.center;
        float halfHeight = Controller.height * 0.5f;
        Vector3 footSample = new Vector3(basePos.x, basePos.y - halfHeight - 0.01f, basePos.z);
        byte blockId = SampleBlock(footSample);
        blockBelowId = blockId;
        return GetSlipperiness(blockId);
    }

    private float GetSlipperiness(byte blockId)
    {
        if (blockId == (byte)BlockMaterial.ICE)
        {
            return 0.98f;
        }

        return DEFAULT_SLIPPERINESS;
    }

    private static Vector3 VelocityFromTick(Vector3 velocityPerTick)
    {
        return velocityPerTick / MC_TICK;
    }

    private bool ShouldBoostUpwardsInFluid(bool water)
    {
        if (IsWalkable)
        {
            return false;
        }

        Vector3 probe = transform.position + Controller.center + Vector3.up * 0.6f;
        if (!IsFluidBlock(SampleBlock(probe), water))
        {
            return false;
        }

        if (lastHorizontalInput.sqrMagnitude < 1e-5f)
        {
            return false;
        }

        return IsSolidAhead(lastHorizontalInput);
    }

    private bool IsSolidAhead(Vector3 direction)
    {
        if (world == null)
        {
            return false;
        }

        Vector3 horizontal = Vector3.ProjectOnPlane(direction, ControllerUp);
        if (horizontal.sqrMagnitude < 1e-5f)
        {
            return false;
        }

        Vector3 origin = transform.position + Controller.center;
        Vector3 dir = horizontal.normalized;
        float probeDistance = Controller.radius + 0.12f;
        float[] heightOffsets = { -0.3f, 0f, 0.3f };

        for (int i = 0; i < heightOffsets.Length; i++)
        {
            Vector3 samplePoint = origin + ControllerUp * heightOffsets[i] + dir * probeDistance;
            byte blockId = SampleBlock(samplePoint);
            if (IsSolidBlock(blockId))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsSolidBlock(byte blockId)
    {
        if (blockId == 0)
        {
            return false;
        }

        if (IsFluidBlock(blockId, true) || IsFluidBlock(blockId, false))
        {
            return false;
        }

        if (world != null && world.blockTypeManager != null)
        {
            return world.blockTypeManager.GetBlockIsSolid(blockId);
        }

        switch ((BlockMaterial)blockId)
        {
            case BlockMaterial.WEB:
            case BlockMaterial.LONG_GRASS:
            case BlockMaterial.DEAD_BUSH:
            case BlockMaterial.FIRE:
            case BlockMaterial.CROPS:
            case BlockMaterial.TORCH:
            case BlockMaterial.REDSTONE_WIRE:
            case BlockMaterial.REDSTONE_TORCH_OFF:
            case BlockMaterial.REDSTONE_TORCH_ON:
            case BlockMaterial.SNOW:
            case BlockMaterial.SUGAR_CANE_BLOCK:
            case BlockMaterial.SIGN_POST:
            case BlockMaterial.WALL_SIGN:
            case BlockMaterial.LADDER:
            case BlockMaterial.LEVER:
            case BlockMaterial.STONE_BUTTON:
            case BlockMaterial.WOOD_PLATE:
            case BlockMaterial.STONE_PLATE:
                return false;
            default:
                return true;
        }
    }

    private bool IsFluidBlock(byte blockId, bool water)
    {
        if (water)
        {
            return blockId == (byte)BlockMaterial.WATER || blockId == (byte)BlockMaterial.STATIONARY_WATER;
        }

        return blockId == (byte)BlockMaterial.LAVA || blockId == (byte)BlockMaterial.STATIONARY_LAVA;
    }

    private void EmitWaterEntryParticles()
    {
        if (particleManager == null || Controller == null)
        {
            return;
        }

        float particleRadius = Controller.radius;
        float particleSurfaceY = Mathf.Floor(transform.position.y) + 1.0f;
        int particleCount = Mathf.CeilToInt(1.0f + particleRadius * 40.0f);

        for (int i = 0; i < particleCount; i++)
        {
            float offsetX = (Random.value * 2.0f - 1.0f) * particleRadius;
            float offsetZ = (Random.value * 2.0f - 1.0f) * particleRadius;
            particleManager.SpawnParticle(
                McParticleManager.PT_BUBBLE,
                transform.position.x + offsetX,
                particleSurfaceY,
                transform.position.z + offsetZ,
                mcVelocity.x,
                mcVelocity.y - Random.value * 0.2f,
                mcVelocity.z
            );
        }

        for (int i = 0; i < particleCount; i++)
        {
            float offsetX = (Random.value * 2.0f - 1.0f) * particleRadius;
            float offsetZ = (Random.value * 2.0f - 1.0f) * particleRadius;
            particleManager.SpawnParticle(
                McParticleManager.PT_SPLASH,
                transform.position.x + offsetX,
                particleSurfaceY,
                transform.position.z + offsetZ,
                mcVelocity.x,
                mcVelocity.y,
                mcVelocity.z
            );
        }
    }
}
