namespace OpenBook.Application.Forms;

/// <summary>
/// Shares a line table's width out among its columns. A column with a weight gets a share of what's left by weight; an
/// auto column gets what its contents need (<c>Need</c>, measured by the renderer), at most <see cref="MaxAutoShare"/>
/// of the table when other columns share it. When auto columns would leave too little, they're narrowed (the text
/// column first, since it can wrap); when every column is auto, the room left over goes to the text column, else to all
/// of them by their needs.
/// </summary>
public static class FormColumnWidths
{
    /// <summary>The most one auto column takes when other columns share the table.</summary>
    public const double MaxAutoShare = 0.6;

    /// <summary>The least a column with a weight is left, in points (unless the table itself is narrower).</summary>
    public const double MinShare = 36;

    /// <param name="columns">Each column's weight, and its measured need when it's auto (else null).</param>
    /// <param name="available">The table's width.</param>
    /// <param name="textIndex">The column descriptions go in (it can wrap), or -1.</param>
    /// <returns>Widths in the same units as <paramref name="available"/>, adding up to it.</returns>
    public static double[] Share(IReadOnlyList<(int Weight, double? Need)> columns, double available, int textIndex)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var widths = new double[columns.Count];
        if (columns.Count == 0 || available <= 0)
            return widths;

        var auto = Enumerable.Range(0, columns.Count).Where(i => columns[i].Need is not null).ToList();
        var weighted = Enumerable.Range(0, columns.Count).Where(i => columns[i].Need is null).ToList();
        var cap = weighted.Count > 0 ? available * MaxAutoShare : available;
        foreach (var i in auto)
            widths[i] = Math.Min(Math.Max(columns[i].Need!.Value, 1), cap);

        // What auto columns may take: everything, or all but a minimum share for each weighted column.
        var room = weighted.Count == 0 ? available : Math.Max(available * 0.25, available - (weighted.Count * MinShare));
        Narrow(widths, auto, room, textIndex);

        if (weighted.Count > 0)
        {
            var left = available - auto.Sum(i => widths[i]);
            var total = weighted.Sum(i => (double)Math.Max(columns[i].Weight, 1));
            foreach (var i in weighted)
                widths[i] = left * Math.Max(columns[i].Weight, 1) / total;
        }
        else if (auto.Sum(i => widths[i]) is var used && used < available)
        {
            if (textIndex >= 0 && textIndex < widths.Length)
                widths[textIndex] += available - used;
            else
                foreach (var i in auto)
                    widths[i] += (available - used) * widths[i] / used;
        }

        return widths;
    }

    /// <summary>Brings the auto columns down to <paramref name="room"/>: the text column first (to a third of the room), then all of them evenly by width.</summary>
    private static void Narrow(double[] widths, List<int> auto, double room, int textIndex)
    {
        var used = auto.Sum(i => widths[i]);
        if (used <= room)
            return;
        if (auto.Contains(textIndex))
        {
            var floor = Math.Min(widths[textIndex], room / 3);
            var cut = Math.Min(used - room, widths[textIndex] - floor);
            widths[textIndex] -= cut;
            used -= cut;
        }

        if (used > room)
            foreach (var i in auto)
                widths[i] *= room / used;
    }
}
