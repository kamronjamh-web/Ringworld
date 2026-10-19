#[vertex]
#version 450

#include "./CloudsInc.comp"
// GenericData: 304 floats (16-slot ring atmosphere profile, P2b); touched so Godot reimports against the new CloudsInc.comp layout.

#define STAGE_VERTEX
#include "./SunshineCloudsDisplay.rast"
#undef STAGE_VERTEX

#[fragment]
#version 450

#include "./CloudsInc.comp"
#include "./SunshineCloudsDisplay.rast"
