#define LOGGING

using UdonSharp;
using UnityEngine;

// Partial of McWorld holding the in-VM scheduler merged from McCoordinator.
// (Phase 1 of the voxel rethink — see docs/superpowers/plans/2026-06-19-voxel-rethink-p0-p1-merge.md)
//
// Task 1 spike: confirm UdonSharp supports partial classes. If this compiles, the
// merged worker state machine / picker / AssignWork land in this file.
public partial class McWorld
{
    private void _PartialClassSpikeNoop() { }
}
