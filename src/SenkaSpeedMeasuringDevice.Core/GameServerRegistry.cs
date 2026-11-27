using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace SenkaSpeedMeasuringDevice.Core;

public static class GameServerRegistry
{
    private static readonly GameServer[] Servers =
    [
        new(1, "横須賀鎮守府", "w01y.kancolle-server.com"),
        new(2, "呉鎮守府", "w02k.kancolle-server.com"),
        new(3, "佐世保鎮守府", "w03s.kancolle-server.com"),
        new(4, "舞鶴鎮守府", "w04m.kancolle-server.com"),
        new(5, "大湊警備府", "w05o.kancolle-server.com"),
        new(6, "トラック泊地", "w06t.kancolle-server.com"),
        new(7, "リンガ泊地", "w07l.kancolle-server.com"),
        new(8, "ラバウル基地", "w08r.kancolle-server.com"),
        new(9, "ショートランド泊地", "w09s.kancolle-server.com"),
        new(10, "ブイン基地", "w10b.kancolle-server.com"),
        new(11, "タウイタウイ泊地", "w11t.kancolle-server.com"),
        new(12, "パラオ泊地", "w12p.kancolle-server.com"),
        new(13, "ブルネイ泊地", "w13b.kancolle-server.com"),
        new(14, "単冠湾泊地", "w14h.kancolle-server.com"),
        new(15, "幌筵泊地", "w15p.kancolle-server.com"),
        new(16, "宿毛湾泊地", "w16s.kancolle-server.com"),
        new(17, "鹿屋基地", "w17k.kancolle-server.com"),
        new(18, "岩川基地", "w18i.kancolle-server.com"),
        new(19, "佐伯湾泊地", "w19s.kancolle-server.com"),
        new(20, "柱島泊地", "w20h.kancolle-server.com")
    ];

    private static readonly FrozenDictionary<int, GameServer> ById =
        Servers.ToFrozenDictionary(server => server.Id);

    public static IReadOnlyList<GameServer> All { get; } = new ReadOnlyCollection<GameServer>(Servers);

    public static bool TryGet(int id, out GameServer server) => ById.TryGetValue(id, out server!);
}
