using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Хвилина гри (<see cref="Clicker.PlayMinute"/>) просто зараз — мірка, якою з 10.10 платять купець, Око, замовлення, віз і гостинці.</summary>
static class ClickerPlay
{
    public static double Minute(RoomHarness h)
    {
        lock (h.Room.Sync) return ((Clicker)h.Room.Game).PlayMinute;
    }

    /// <summary>Стільки хвилин гри — у глеках, як рахує гра (вниз до цілого).</summary>
    public static double Pay(RoomHarness h, double minutes) => Math.Floor(Minute(h) * minutes);
}
