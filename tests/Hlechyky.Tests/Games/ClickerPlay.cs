using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Хвилина гри для нагород (<see cref="Clicker.PayMinute"/>, з 08.11 — на своїй чорній глині, хоч що на колі) — мірка, якою з 10.10 платять купець, Око, замовлення, віз і гостинці.</summary>
static class ClickerPlay
{
    public static double Minute(RoomHarness h)
    {
        lock (h.Room.Sync) return ((Clicker)h.Room.Game).PayMinute;
    }

    /// <summary>Стільки хвилин гри — у глеках, як рахує гра (вниз до цілого).</summary>
    public static double Pay(RoomHarness h, double minutes) => Math.Floor(Minute(h) * minutes);
}
