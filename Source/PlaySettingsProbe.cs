using System;
using System.Globalization;
using System.Text;
using RimWorld;
using Verse;

namespace TurboTick;

/// <summary>Measures the eleven `defaultCareFor*` settings on the play-settings row. The defaults are read by the med-care
/// resolution for every pawn that has no explicit setting (`:477541-477580`), so a replica showing a different default is
/// showing a setting its player never made.
///
/// <para>Argument: `&lt;字段序号或名字后缀&gt; &lt;等级&gt;`, e.g. `prisoner 3` or `2 3`.</para></summary>
internal static class PlaySettingsProbe
{
    internal static string Run(string argument)
    {
        var parts = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "用法：<字段序号或名字后缀> <等级>，例如 prisoner 3";
        var index = -1;
        for (var i = 0; i < PlaySettingsSync.CareFields.Length && index < 0; i++)
            if (PlaySettingsSync.CareFields[i].EndsWith(parts[0], StringComparison.OrdinalIgnoreCase)) index = i;
        if (index < 0) return "没有这个字段：" + parts[0];
        // Byte-backed enum: compare against its last member rather than asking `Enum.IsDefined` with an int.
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
            || level < 0 || level > (int)MedicalCareCategory.Best)
            return "等级不是 MedicalCareCategory 的值（0–" + (int)MedicalCareCategory.Best + "）：" + parts[1];

        var settings = Find.PlaySettings;
        var field = typeof(PlaySettings).GetField(PlaySettingsSync.CareFields[index]);
        if (field is null) return "字段反射失败：" + PlaySettingsSync.CareFields[index];
        var sb = new StringBuilder("默认医疗 " + PlaySettingsSync.CareFields[index] + "：");
        var before = (MedicalCareCategory)field.GetValue(settings);
        field.SetValue(settings, (MedicalCareCategory)level);
        var rows = PlaySettingsSync.Publish();
        var care = rows is { Count: > 0 } ? rows[0].Care : null;
        var listed = care?.Split(',');
        sb.Append($"原 {before} → 设为 {(MedicalCareCategory)level}；行 Care=" + (care ?? "无") +
                  $"（第 {index} 段应 {level}：" + (listed is not null && index < listed.Length ? listed[index] : "缺") + "）");
        // Restore without publishing, so only the row can put the new value back.
        field.SetValue(settings, before);
        var readBackBefore = (MedicalCareCategory)field.GetValue(settings);
        if (rows is { Count: > 0 }) PlaySettingsSync.Apply(rows[0]);
        var readBack = (MedicalCareCategory)field.GetValue(settings);
        sb.Append($"；不发地写回旧值后读 {(int)readBackBefore}，应用该行后读 {(int)readBack}（应 {(int)level}）");
        field.SetValue(settings, before);
        sb.Append($"，已复原 {(int)(MedicalCareCategory)field.GetValue(settings)}；" + PlaySettingsSync.Summary());
        return sb.ToString();
    }
}
