// World generation feature mask (T1, plan 2.2). WorldGeneration.Publish sets ringworld_world_features at world load.
// A shader gates a look on a feature with ring_world_has(bit); the bits are the ledger's (WorldGenerationLedger.Production).
#ifndef RING_WORLD_FEATURES
#define RING_WORLD_FEATURES
global uniform int ringworld_world_features;
bool ring_world_has(int bit) {
	return (ringworld_world_features & (1 << bit)) != 0;
}
#endif
