using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DungeonDepthPrototype;

// Возврат распределённых очков штатными методами закреплённого PoV, без удаления опыта.
internal static class PovRespec
{
    private const string Key = "uberdungeon.qa.pov-refund.090";
    private static object Progress => AccessTools.Property(PovBridge.Assembly.GetType("PassiveTree.Progress", true),
        "Current").GetValue(null) ?? throw new InvalidOperationException("PoV ещё не загрузил персонажа");
    private static object Field(object value, string name) => AccessTools.Field(value.GetType(), name).GetValue(value);
    private static object Property(object value, string name) =>
        AccessTools.Property(value.GetType(), name).GetValue(value);

    internal static JObject Status()
    {
        if (!ForestManual.Target(Player.m_localPlayer)) throw new InvalidOperationException("Нужен тестовый Drug");
        var progress = Progress;
        var craft = Field(progress, "Craft");
        return JObject.FromObject(new
        {
            combat = new { total = Property(progress, "TotalPoints"), spent = Property(progress, "SpentPoints"),
                available = Property(progress, "AvailablePoints"), level = Field(progress, "Level"),
                xp = Field(progress, "Xp"), bonus = Field(progress, "BonusPoints") },
            craft = new { total = Property(craft, "Points"), spent = Property(craft, "Spent"),
                available = Property(craft, "Available"), level = Field(craft, "Level"),
                xp = Field(craft, "Xp"), total_xp = Property(craft, "TotalXp") },
            refunded = Player.m_localPlayer.m_customData.ContainsKey(Key)
        });
    }

    internal static object Refund()
    {
        var player = Player.m_localPlayer;
        if (!ForestManual.Target(player) || player.IsTeleporting() || player.IsDead())
            throw new InvalidOperationException("Возврат очков требует готового тестового персонажа");
        if (player.m_customData.ContainsKey(Key)) return Status();
        var before = Status();
        var snapshot = new Dictionary<string, string>(player.m_customData);
        var folder = Path.Combine(Paths.ConfigPath, "ForestCryptQA");
        Directory.CreateDirectory(folder);
        var receipt = Path.Combine(folder, "pov-refund-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json");
        File.WriteAllText(receipt, JsonConvert.SerializeObject(new { before, custom_data = snapshot },
            Formatting.Indented));
        var progress = Progress;
        var craft = Field(progress, "Craft");
        try
        {
            AccessTools.Method(progress.GetType(), "ResetTreeFree").Invoke(progress, null);
            AccessTools.Method(craft.GetType(), "Respec").Invoke(craft, null);
            var after = Status();
            foreach (string tree in new[] { "combat", "craft" })
            {
                foreach (string field in tree == "combat" ? new[] { "total", "level", "xp", "bonus" } :
                    new[] { "total", "level", "xp", "total_xp" })
                    if (!JToken.DeepEquals(before[tree]![field], after[tree]![field]))
                        throw new InvalidOperationException("Сброс изменил заработанное: " + tree + "." + field);
                if (after[tree]!.Value<int>("spent") != 0 ||
                    after[tree]!.Value<int>("available") != before[tree]!.Value<int>("total"))
                    throw new InvalidOperationException("Не все очки возвращены: " + tree);
            }
            player.m_customData[Key] = DateTime.UtcNow.ToString("O");
            Game.instance.SavePlayerProfile(true);
            File.WriteAllText(receipt + ".verified", JsonConvert.SerializeObject(new { before, after },
                Formatting.Indented));
            return new { before, after, passed = true };
        }
        catch
        {
            player.m_customData.Clear();
            foreach (var pair in snapshot) player.m_customData[pair.Key] = pair.Value;
            AccessTools.Method(progress.GetType(), "Load").Invoke(progress, null);
            AccessTools.Method(progress.GetType(), "AfterChange").Invoke(progress, null);
            throw;
        }
    }
}
