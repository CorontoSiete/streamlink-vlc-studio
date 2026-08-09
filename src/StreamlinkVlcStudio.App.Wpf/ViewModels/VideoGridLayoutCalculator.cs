namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal static class VideoGridLayoutCalculator
{
    public const int TileLimit = 16;
    public const int BaseGridSize = 2;
    public const int MaxGridSize = 4;

    public static VideoGridLayout GetLayout(int visibleCount)
    {
        var boundedCount = Math.Clamp(visibleCount, 1, TileLimit);
        if (boundedCount == 6)
        {
            return new VideoGridLayout(2, 3);
        }

        if (boundedCount == 8)
        {
            return new VideoGridLayout(2, 4);
        }

        if (boundedCount == 10)
        {
            return new VideoGridLayout(2, 5);
        }

        if (boundedCount == 12)
        {
            return new VideoGridLayout(3, 4);
        }

        if (boundedCount <= BaseGridSize * BaseGridSize)
        {
            return new VideoGridLayout(BaseGridSize, BaseGridSize);
        }

        var rows = boundedCount <= 8
            ? 2
            : boundedCount <= 12
                ? 3
                : MaxGridSize;
        var columns = Math.Clamp(
            (int)Math.Ceiling((double)boundedCount / rows),
            BaseGridSize,
            MaxGridSize);
        return new VideoGridLayout(rows, columns);
    }

    public static VideoPlacement GetPlacement(int index, int visibleCount, VideoGridLayout layout)
    {
        if (visibleCount <= 1)
        {
            return new VideoPlacement(Row: 0, Column: 0, RowSpan: layout.Rows, ColumnSpan: layout.Columns);
        }

        if (visibleCount == 2)
        {
            return new VideoPlacement(Row: 0, Column: index, RowSpan: layout.Rows, ColumnSpan: 1);
        }

        return new VideoPlacement(
            Row: index / layout.Columns,
            Column: index % layout.Columns,
            RowSpan: 1,
            ColumnSpan: 1);
    }
}

internal readonly record struct VideoGridLayout(int Rows, int Columns);

internal readonly record struct VideoPlacement(int Row, int Column, int RowSpan, int ColumnSpan);
