using System.Text.Json;

namespace Hlechyky.Games.Impl;

// Арсенал морського бою — заготовка: режим «⚓ Арсенал» приходить наступним кроком.
public sealed partial class Battleship
{
    void ConfigureArsenal(IReadOnlyDictionary<string, string> options) { }
    void StartArsenal() { }
    ActResult Buy(int seat, JsonElement payload) => ActResult.Fail("Арсенал — лише в режимі «⚓ Арсенал»");
    ActResult Sell(int seat, JsonElement payload) => ActResult.Fail("Арсенал — лише в режимі «⚓ Арсенал»");
    ActResult Use(int seat, JsonElement payload) => ActResult.Fail("Арсенал — лише в режимі «⚓ Арсенал»");
    void DeployMines() { }
    bool TripMine(int by, int at, int cell, Shot? note) => false;
    void Unpatch(int at, int cell) { }
    bool BotTool(int seat, int at) => false;
    void SettleArsenal() { }
    string Ledger() => "";
    int[]? MinesOf(int seat) => null;
    int[]? BoomOf(int seat) => null;
    int[]? PatchedOf(int seat) => null;
    object? ArsenalView(int mine) => null;
    string? SaveArsenal() => null;
    void LoadArsenal(string? json) { }
}
