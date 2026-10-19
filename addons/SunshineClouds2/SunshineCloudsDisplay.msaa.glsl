#[vertex]
#version 450

#define MSAA_ENABLED 1

#include "./CloudsInc.comp"
// GenericData: 312 floats (16-slot ring atmosphere profile P2b + enclosed fog P6 + engine jitter controls); touched so Godot reimports against the new CloudsInc.comp layout.

#define STAGE_VERTEX
#include "./SunshineCloudsDisplay.rast"
#undef STAGE_VERTEX

#[fragment]
#version 450

#define MSAA_ENABLED 1

#include "./CloudsInc.comp"
#include "./SunshineCloudsDisplay.rast"
