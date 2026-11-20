using System;
using System.Collections.Generic;
using HarmonyLib;

namespace TurboTick;

/// <summary>Resolves a type by namespace-qualified name, then by simple name over every loaded assembly.
///
/// <para><b>Why a fallback exists (round 140).</b> `GizmoStatePatch.GizmoTargetNames` lists fourteen gizmo types by
/// qualified name, and one of them - `Command_CallBossgroup` - had the wrong namespace since it was written. Nothing
/// could catch that: the names are strings, so the compiler never sees them, Harmony only resolves them at runtime, and
/// the miss was reported as one warning line that said "this type has no `ProcessInput` to patch". The cost was that the
/// summon gizmo's click was never captured on any peer, and the symptom looked like the separate cooldown bug.
///
/// <para>Falling back to the simple name is safe here because the list is a closed set of vanilla types whose simple
/// names are unique in the game assembly; it is also what makes the list survive a namespace move, which is the exact
/// kind of change that produced the defect. The audit prints which entries needed the fallback, so a name that is wrong
/// in a way the fallback hides is still visible.</para></summary>
internal static class TypeResolver
{
    /// <summary>The type, or null. Never throws: a missing type is a missing feature, not a crash.</summary>
    internal static Type? Find(string qualifiedName)
    {
        if (string.IsNullOrEmpty(qualifiedName)) return null;
        var direct = AccessTools.TypeByName(qualifiedName);
        if (direct is not null) return direct;
        var simple = qualifiedName[(qualifiedName.LastIndexOf('.') + 1)..];
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (Exception) { continue; }
            foreach (var candidate in types)
                if (string.Equals(candidate.Name, simple, StringComparison.Ordinal)) return candidate;
        }
        return null;
    }

    /// <summary>Whether the qualified name resolved without the simple-name fallback (the namespace is right).</summary>
    internal static bool Exact(string qualifiedName) => AccessTools.TypeByName(qualifiedName) is not null;
}
