using System;
using System.Collections.Generic;

namespace TurboTick.Networking;

/// <summary>Text surgery on Scribe fragments.
///
/// <para><b>Why this is not in the mod assembly.</b> It is pure string work with no game types, and the rule it enforces
/// is the kind of thing that has to be nailed down by a test rather than by a comment. The test project references only
/// the networking and simulation assemblies, so the helper lives here and the game-side channel calls it.</para>
///
/// <para><b>The rule.</b> A channel may compare a stripped fragment, but it must send the whole one. `Scribe_Defs.Look`
/// is <c>value = ScribeExtractor.DefFromNode(curXmlParent[label])</c> and `DefFromNode` returns <b>null</b> for a missing
/// node (`:114517`), so loading a fragment whose <c>&lt;def&gt;</c> was stripped does not "leave the field alone" — it
/// writes null into a live object. Measured consequence on 0.7.3: a settlement lost its def on the first apply, and
/// `Map.MapEdgeMaterial` (`:76530`, <c>parent.def.MapEdgeMaterial</c>) and `Map.IsPlayerHome` (`:76546`,
/// <c>info.parent.def.canBePlayerHome</c>) then threw NRE every frame in the client's draw path.</para></summary>
public static class FragmentText
{
    /// <summary>Removes the first <c>&lt;tag&gt;…&lt;/tag&gt;</c> (self-closing or not) from a fragment.
    ///
    /// <para>Written against the text because the fields live in private members the game only writes through
    /// <c>ExposeData</c>; building a filtered copy of the object would mean writing to the running colony.</para></summary>
    public static string Strip(string xml, string tag)
    {
        if (string.IsNullOrEmpty(xml)) return xml;
        var open = xml.IndexOf("<" + tag, StringComparison.Ordinal);
        if (open < 0) return xml;
        // The opening tag may carry attributes (`<questTags IsNull="True" />`), so the end of the tag is located rather
        // than assuming a bare form.
        var tagEnd = xml.IndexOf('>', open);
        if (tagEnd < 0) return xml;
        if (xml[tagEnd - 1] == '/') return xml.Remove(open, tagEnd - open + 1);
        var close = xml.IndexOf("</" + tag + ">", tagEnd, StringComparison.Ordinal);
        if (close < 0) return xml;
        return xml.Remove(open, close + tag.Length + 3 - open);
    }

    /// <summary>Strips every tag in the list, in order.</summary>
    public static string Strip(string xml, IReadOnlyList<string> tags)
    {
        foreach (var tag in tags) xml = Strip(xml, tag);
        return xml;
    }
}
