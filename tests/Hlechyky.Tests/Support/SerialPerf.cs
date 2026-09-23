namespace Hlechyky.Tests.Support;

/// <summary>
/// Тести, що міряють швидкодію стінним годинником. У повному паралельному прогоні всі ядра зайняті й годинник
/// бреше в рази — тож такі класи йдуть окремою колекцією, яку xUnit запускає вже без сусідів.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialPerf
{
    public const string Name = "Швидкодія — без паралелі";
}
