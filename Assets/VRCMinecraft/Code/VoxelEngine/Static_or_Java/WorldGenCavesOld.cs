using UnityEngine;

public class WorldGenCavesOld {

    private int maxGenerationRadius;
    private JavaRandom rand;

    public const double MIN_HORIZONTAL_SIZE = 1.5D;

    // PARITY+PERF: Beta's `MathHelper.SIN_TABLE` — a 65,536-entry float LUT used by
    // cave shaping (and Beta-wide). Matching this LUT gets us closer to Beta cave
    // shapes than Unity's `Mathf.Sin` (which routes through `System.Math.Sin` and
    // would not have the same 16-bit quantization).
    // Note: UdonSharp does not support static fields on user-defined types, so this
    // is an instance field. 256 KB one-time alloc per WorldGenCavesOld instance.
    private readonly float[] _sinTable = new float[65536];
    private bool _sinTableReady = false;

    public WorldGenCavesOld() {
        maxGenerationRadius = 8;
        rand = new JavaRandom();
        _InitSinTable();
    }

    private void _InitSinTable() {
        if (_sinTableReady) return;
        for (int i = 0; i < 65536; i++) {
            _sinTable[i] = (float)System.Math.Sin((double)i * System.Math.PI * 2.0 / 65536.0);
        }
        _sinTableReady = true;
    }

    private float _Sin(float x) {
        return _sinTable[(int)(x * 10430.378F) & 0xFFFF];
    }

    private float _Cos(float x) {
        // cos(x) = sin(x + PI/2) ; +16384 indices = quarter turn in the 65536-entry table
        return _sinTable[(int)(x * 10430.378F + 16384.0F) & 0xFFFF];
    }

    protected void generateDefaultBranch(McWorld world, int generatedChunkX, int generatedChunkZ, double structureOriginBlockX, double structureOriginBlockY, double structureOriginBlockZ) {
        generateBranch(
                world, generatedChunkX, generatedChunkZ,
                structureOriginBlockX, structureOriginBlockY, structureOriginBlockZ,
                1.0F + rand.NextFloat() * 6F, 0.0F, 0.0F,
                -1, -1, 0.5D);
    }

    protected void generateBranch(McWorld world, int generatedChunkX, int generatedChunkZ,
                                  double currentBlockX, double currentBlockY, double currentBlockZ,
                                  float maxHorizontalSize, float directionAngleHorizontal, float directionAngleVertical,
                                  int currentCaveSystemRadius, int maxCaveSystemRadius, double verticalCaveSizeMultiplier) {

        double generatedChunkCenterX = generatedChunkX * 16 + 8;
        double generatedChunkCenterZ = generatedChunkZ * 16 + 8;
        float directionHorizontalChange = 0.0F;
        float directionVerticalChange = 0.0F;
        JavaRandom random = new JavaRandom(rand.NextLong());
        //negative means not generated yet
        if (maxCaveSystemRadius <= 0) {
            int maxBlockRadius = maxGenerationRadius * 16 - 16;
            maxCaveSystemRadius = maxBlockRadius - random.NextInt(maxBlockRadius / 4);
        }
        bool noSplitBranch = false;
        if (currentCaveSystemRadius == -1) {
            currentCaveSystemRadius = maxCaveSystemRadius / 2;
            noSplitBranch = true;
        }
        int splitDistance = random.NextInt(maxCaveSystemRadius / 2) + maxCaveSystemRadius / 4;
        bool allowSteepCave = random.NextInt(6) == 0;
        for (; currentCaveSystemRadius < maxCaveSystemRadius; currentCaveSystemRadius++) {

            //caveRadius grows as we go out of the center
            double caveRadiusHorizontal = MIN_HORIZONTAL_SIZE + (double) (_Sin((float) currentCaveSystemRadius * 3.141593F / (float) maxCaveSystemRadius) * maxHorizontalSize * 1.0f);
            double caveRadiusVertical = caveRadiusHorizontal * verticalCaveSizeMultiplier;

            //from sin(alpha)=y/r and cos(alpha)=x/r ==> x = r*cos(alpha) and y = r*sin(alpha)
            //always moves by one block in some direction
            //x is horizontal radius, y is vertical
            float horizontalDirectionSize = _Cos(directionAngleVertical);
            float directionY = _Sin(directionAngleVertical);
            //y is directionZ and is is directionX
            currentBlockX += _Cos(directionAngleHorizontal) * horizontalDirectionSize;
            currentBlockY += directionY;
            currentBlockZ += _Sin(directionAngleHorizontal) * horizontalDirectionSize;
            if (allowSteepCave) {
                directionAngleVertical *= 0.92F;
            } else {
                directionAngleVertical *= 0.7F;
            }
            directionAngleVertical += directionVerticalChange * 0.1F;
            directionAngleHorizontal += directionHorizontalChange * 0.1F;

            directionVerticalChange *= 0.9F;
            directionHorizontalChange *= 0.75F;
            directionVerticalChange += (random.NextFloat() - random.NextFloat()) * random.NextFloat() * 2.0F;
            directionHorizontalChange += (random.NextFloat() - random.NextFloat()) * random.NextFloat() * 4F;

            if (!noSplitBranch && currentCaveSystemRadius == splitDistance && maxHorizontalSize > 1.0F) {
                generateBranch(world, generatedChunkX, generatedChunkZ, currentBlockX, currentBlockY, currentBlockZ,
                        random.NextFloat() * 0.5F + 0.5F, directionAngleHorizontal - 1.570796F,
                        directionAngleVertical / 3F, currentCaveSystemRadius, maxCaveSystemRadius, 1.0D);
                generateBranch(world, generatedChunkX, generatedChunkZ, currentBlockX, currentBlockY, currentBlockZ,
                        random.NextFloat() * 0.5F + 0.5F, directionAngleHorizontal + 1.570796F,
                        directionAngleVertical / 3F, currentCaveSystemRadius, maxCaveSystemRadius, 1.0D);
                return;
            }
            if (!noSplitBranch && random.NextInt(4) == 0) {
                continue;
            }
            double chunkCenterToCurrentX = currentBlockX - generatedChunkCenterX;
            double chunkCenterToCurrentZ = currentBlockZ - generatedChunkCenterZ;

            if (isCurrentChunkUnreachable(chunkCenterToCurrentX, chunkCenterToCurrentZ, maxCaveSystemRadius, currentCaveSystemRadius, maxHorizontalSize)) {
                return;
            }
            //is cave out of bounds of current chunk?
            if (currentBlockX < generatedChunkCenterX - 16D - caveRadiusHorizontal * 2D ||
                    currentBlockZ < generatedChunkCenterZ - 16D - caveRadiusHorizontal * 2D ||
                    currentBlockX > generatedChunkCenterX + 16D + caveRadiusHorizontal * 2D ||
                    currentBlockZ > generatedChunkCenterZ + 16D + caveRadiusHorizontal * 2D) {
                continue;
            }
            int startX = Mathf.FloorToInt((float)(currentBlockX - caveRadiusHorizontal)) - generatedChunkX * 16 - 1;
            int endX = (Mathf.FloorToInt((float)(currentBlockX + caveRadiusHorizontal)) - generatedChunkX * 16) + 1;
            int startY = Mathf.FloorToInt((float)(currentBlockY - caveRadiusVertical)) - 1;
            int endY = Mathf.FloorToInt((float)(currentBlockY + caveRadiusVertical)) + 1;
            int startZ = Mathf.FloorToInt((float)(currentBlockZ - caveRadiusHorizontal)) - generatedChunkZ * 16 - 1;
            int endZ = (Mathf.FloorToInt((float)(currentBlockZ + caveRadiusHorizontal)) - generatedChunkZ * 16) + 1;
            if (startX < 0) {
                startX = 0;
            }
            if (endX > 16) {
                endX = 16;
            }
            if (startY < 1) {
                startY = 1;
            }
            if (endY > 120) {
                endY = 120;
            }
            if (startZ < 0) {
                startZ = 0;
            }
            if (endZ > 16) {
                endZ = 16;
            }

            if (findWater(world, generatedChunkX, generatedChunkZ, startX, endX, startY, endY, startZ, endZ)) {
                continue;
            }
            for (int localX = startX; localX < endX; localX++) {
                double xDistanceScaled = ((double)(localX + generatedChunkX * 16) + 0.5D - currentBlockX) / caveRadiusHorizontal;
                for (int localZ = startZ; localZ < endZ; localZ++) {
                    double zDistanceScaled = ((double)(localZ + generatedChunkZ * 16) + 0.5D - currentBlockZ) / caveRadiusHorizontal;
                    bool hitGrassSurface = false;
                    if (xDistanceScaled * xDistanceScaled + zDistanceScaled * zDistanceScaled >= 1.0D) {
                        continue;
                    }

                    // PARITY: Java iterates `var48 = var36 - 1; var48 >= var54; --var48` (MapGenCaves.java).
                    // Block reads/writes happen at `localY + 1`, so starting localY = endY - 1 carves blocks at
                    // Y range [startY+1, endY], matching Java. The old `localY = endY` carved one block too high.
                    for (int localY = endY - 1; localY >= startY; localY--) {
                        double yDistanceScaled = (((double)localY + 0.5D) - currentBlockY) / caveRadiusVertical;
                        //yDistanceScaled > -0.7 ==> flattened floor
                        if (yDistanceScaled > -0.7D &&
                                xDistanceScaled * xDistanceScaled + yDistanceScaled * yDistanceScaled + zDistanceScaled * zDistanceScaled < 1.0D) {
                            // Convert local coordinates to global coordinates
                            int globalX = localX + generatedChunkX * 16;
                            int globalY = localY;
                            int globalZ = localZ + generatedChunkZ * 16;
                            
                            byte previousBlock = (byte)(world.GetBlock(globalX, localY + 1, globalZ) & 0xFF);
                            if (previousBlock == (byte)BlockMaterial.GRASS) {
                                hitGrassSurface = true;
                            }
                            if (previousBlock == (byte)BlockMaterial.STONE
                                    || previousBlock == (byte)BlockMaterial.DIRT
                                    || previousBlock == (byte)BlockMaterial.GRASS) {
                                if (localY < 10) {
                                    world.SetBlock(globalX, localY + 1, globalZ, (byte)BlockMaterial.STATIONARY_LAVA);
                                } else {
                                    world.SetBlock(globalX, localY + 1, globalZ, (byte)BlockMaterial.AIR);
                                    if (hitGrassSurface && (byte)(world.GetBlock(globalX, localY, globalZ) & 0xFF) == (byte)BlockMaterial.DIRT) {
                                        world.SetBlock(globalX, localY, globalZ, (byte)BlockMaterial.GRASS);
                                    }
                                }
                            }
                        }

                    }
                }
            }

            //why?
            if (noSplitBranch) {
                break;
            }
        }

    }

    //returns true of this distance can't be reached even after all remaining iterations
    private static bool isCurrentChunkUnreachable(double distanceToOriginX, double distanceToOriginZ, int maxCaveSystemRadius, int currentCaveSystemRadius, float maxHorizontalSize) {
        double blocksLeft = maxCaveSystemRadius - currentCaveSystemRadius;
        //even if the exact block can't be reached, the chunk may be reachable by center of the cave
        //and cave size must be also included
        double bufferDistance = maxHorizontalSize + 2.0F + 16F;
        return (distanceToOriginX * distanceToOriginX + distanceToOriginZ * distanceToOriginZ) - blocksLeft * blocksLeft > bufferDistance * bufferDistance;
    }

    private bool findWater(McWorld world, int generatedChunkX, int generatedChunkZ, int startX, int endX, int startY, int endY, int startZ, int endZ) {
        for (int xPos = startX; xPos < endX; xPos++) {
            for (int zPos = startZ; zPos < endZ; zPos++) {
                for (int yPos = endY + 1; yPos >= startY - 1; yPos--) {
                    if (yPos < 0 || yPos >= 128) {
                        continue;
                    }
                    // Convert local coordinates to global coordinates
                    int globalX = xPos + generatedChunkX * 16;
                    int globalY = yPos;
                    int globalZ = zPos + generatedChunkZ * 16;
                    
                    byte blockType = (byte)(world.GetBlock(globalX, globalY, globalZ) & 0xFF);
                    if (blockType == (byte)BlockMaterial.WATER || blockType == (byte)BlockMaterial.STATIONARY_WATER) {
                        return true;
                    }
                    if (yPos != startY - 1 && xPos != startX && xPos != endX - 1
                            && zPos != startZ && zPos != endZ - 1) {
                        yPos = startY;
                    }
                }

            }
        }
        return false;
    }

    public void Generate(McWorld world, int structureOriginChunkX, int structureOriginChunkZ, int generatedChunkX, int generatedChunkZ) {
        int attempts = rand.NextInt(rand.NextInt(rand.NextInt(40) + 1) + 1);
        if (rand.NextInt(15) != 0) {
            attempts = 0;
        }
        for (int i = 0; i < attempts; i++) {
            double structureOriginBlockX = structureOriginChunkX * 16 + rand.NextInt(16);
            double structureOriginBlockY = rand.NextInt(rand.NextInt(120) + 8);
            double structureOriginBlockZ = structureOriginChunkZ * 16 + rand.NextInt(16);
            int branches = 1;
            if (rand.NextInt(4) == 0) {
                generateDefaultBranch(world, generatedChunkX, generatedChunkZ, structureOriginBlockX, structureOriginBlockY, structureOriginBlockZ);
                branches += rand.NextInt(4);
            }
            for (int branch = 0; branch < branches; branch++) {
                float directionAndgeHorizontal = rand.NextFloat() * 3.141593F * 2.0F;
                float directionAngleVertical = ((rand.NextFloat() - 0.5F) * 2.0F) / 8F;
                float maxHorizontalSize = rand.NextFloat() * 2.0F + rand.NextFloat();
                generateBranch(world, generatedChunkX, generatedChunkZ, structureOriginBlockX, structureOriginBlockY, structureOriginBlockZ, maxHorizontalSize, directionAndgeHorizontal, directionAngleVertical, 0, 0, 1.0D);
            }
        }
    }
}