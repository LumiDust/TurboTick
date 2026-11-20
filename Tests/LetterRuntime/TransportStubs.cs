// Transport boundary stubs for the letter runtime test.
//
// The artifact under test is the real Source/LetterChoicePatch.cs, compiled unchanged. Only the two network
// publishers and the two session flags it reads are stubbed, so the patch logic runs against the real game
// Letter/DiaOption/Dialog_NodeTree/WindowStack types without booting a colony or a socket.
using System.Collections.Generic;

namespace TurboTick;

internal static class SharedActions
{
    internal static bool PlayerActing = true;

    internal static readonly List<(int LetterId, int Option)> LetterChoices = [];

    internal static readonly List<(int LetterId, List<string> Passions, string? TraitDef, int Degree)> GrowthChoices = [];

    internal static void PublishLetterChoice(int letterId, int option) => LetterChoices.Add((letterId, option));

    internal static void PublishGrowthChoice(int letterId, List<string> passions, string? traitDef, int traitDegree) =>
        GrowthChoices.Add((letterId, passions, traitDef, traitDegree));
}

internal static class AuthoritativeReplica
{
    internal static bool Frozen = true;
}
