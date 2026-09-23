#[compute]
// GenericData ABI: 180 floats, including opt-in menu callback-history controls.
#version 450

#include "./CloudsInc.comp"
// Sunshine's shared post pass uses the ring-local cloud frame.
// The shared body also guards rounded-up dispatches for odd Arc viewport sizes
// and exposes opt-in raw/depth density mattes for Certified-ring diagnostics.
// Wrapper touched with the shared post source: force Godot to reimport the
// pixel-centre reconstruction and native four-tap blur path in the shared
// SunshineCloudsPostCompute.comp source.
#include "./SunshineCloudsPostCompute.comp"
