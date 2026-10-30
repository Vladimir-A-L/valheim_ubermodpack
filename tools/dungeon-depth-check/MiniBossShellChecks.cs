using DungeonDepthPrototype;

internal static class MiniBossShellChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var faces = MiniBossShellPlan.Faces().ToArray();
        var edges = new Dictionary<(string, string), int>();
        string Key(MiniBossShellPlan.Point p) => FormattableString.Invariant($"{p.X:F4},{p.Y:F4},{p.Z:F4}");
        foreach (var face in faces)
        for (int i = 0; i < 4; i++)
        {
            var edge = (Key(face.Points[i]), Key(face.Points[(i + 1) % 4]));
            edges[edge] = edges.GetValueOrDefault(edge) + 1;
        }
        check(edges.All(e => e.Value == 1 && edges.GetValueOrDefault((e.Key.Item2, e.Key.Item1)) == 1),
            "Видимая оболочка замкнута: каждое ребро имеет ровно одну встречную грань, без наложений");
        check(faces.Count(f => f.Cap) == 1, "Открывается только одна грань заглушки стыка");
        var cap = faces.Single(f => f.Cap);
        check(cap.Points.Min(p => p.X) == -1 && cap.Points.Max(p => p.X) == 1 &&
            cap.Points.Min(p => p.Y) == 0 && cap.Points.Max(p => p.Y) == 2.4f,
            "Снимаемая часть совпадает с согласованным проходом 2×2,4 м");
        check(faces.SelectMany(f => f.Points).All(p => p.Y >= -.6f && p.Y <= 6.6f) &&
            cap.Points.All(p => Math.Abs(p.Z + 28.4f) < .001f),
            "Подложка охватывает переход и находится за видимыми плитами");
    }
}
