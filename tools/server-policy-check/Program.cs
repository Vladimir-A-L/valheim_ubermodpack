using System.Text.Json;
using ServerManager;

// Проверяется настоящий валидатор закреплённой ServerManager.dll, без запуска игрового клиента.
// В required обязательно нужен эталон самого ServerManager: игровой сервер добавляет его автоматически.
if (args.Length != 2)
{
    Console.Error.WriteLine("Нужны каталог ServerManager с required/optional и JSON массива клиентских записей.");
    return 2;
}
if (!Directory.Exists(Path.Combine(args[0], "required")) ||
    !Directory.Exists(Path.Combine(args[0], "optional")))
    throw new InvalidOperationException("Нет каталогов эталонов: проверка не создаёт новую политику");
var store = new IntegrityPolicyStore(args[0]);
var loaded = store.TryReload();
if (!loaded.Success || store.Current == null)
    throw new InvalidOperationException("Эталонные DLL не образуют корректную политику");
if (!store.Current.TryGetRule("sighsorry.ServerManager", out _))
    throw new InvalidOperationException("Для автономной проверки требуется эталон ServerManager.dll в required");
using var input = JsonDocument.Parse(File.ReadAllText(args[1]));
var entries = input.RootElement.EnumerateArray().Select(e => new IntegrityManifestEntry(
    e.GetProperty("guid").GetString()!, e.GetProperty("name").GetString()!,
    e.GetProperty("sha256").GetString()!)).ToArray();
var result = IntegrityValidator.Validate(store.Current, new IntegrityManifest(entries));
Console.WriteLine(JsonSerializer.Serialize(new {
    observed_at_utc = DateTime.UtcNow.ToString("O"), allowed = result.Allowed,
    policy_rules = store.Current.Rules.Count, client_records = entries.Length,
    diagnostics = result.Diagnostics.Select(d => new { d.Code, d.PluginGuid }),
    scope = "pinned-assembly-manifest-validation; not-a-game-connection"
}, new JsonSerializerOptions { WriteIndented = true }));
return result.Allowed ? 0 : 1;
