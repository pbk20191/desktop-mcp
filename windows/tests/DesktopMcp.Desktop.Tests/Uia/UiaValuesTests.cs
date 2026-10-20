using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using DesktopMcp.Desktop.Uia;
using Windows.Win32.System.Com;

namespace DesktopMcp.Desktop.Tests.Uia;

public unsafe partial class UiaValuesTests
{
    private const ushort VtI4 = 3;
    private const ushort VtR8 = 5;

    [Fact]
    public void ReadsADoubleArray()
    {
        var array = Create(VtR8, [1.5, -2.25, 3]);
        try { Assert.Equal([1.5, -2.25, 3], UiaValues.ReadDoubles(array)); }
        finally { SafeArrayDestroy(array); }
    }

    [Fact]
    public void ReadsAnIntArray()
    {
        var array = Create(VtI4, [7, 42]);
        try { Assert.Equal([7, 42], UiaValues.ReadInts(array)); }
        finally { SafeArrayDestroy(array); }
    }

    [Fact]
    public void AnArrayOfTheWrongTypeReadsAsEmpty()
    {
        // A provider in another process chose the type: a VT_I4 array read as doubles would run past its end.
        var ints = Create(VtI4, [1, 2, 3]);
        var doubles = Create(VtR8, [1.0]);
        try
        {
            Assert.Empty(UiaValues.ReadDoubles(ints));
            Assert.Empty(UiaValues.ReadInts(doubles));
        }
        finally
        {
            SafeArrayDestroy(ints);
            SafeArrayDestroy(doubles);
        }
    }

    [Fact]
    public void ClickablePointReadsTwoDoubles()
    {
        var array = Create(VtR8, [10.5, 21.5]);
        try { Assert.Equal(new ScreenPoint(10, 22), UiaValues.ToPoint(UiaValues.ReadDoubles(array))); }
        finally { SafeArrayDestroy(array); }
    }

    [Fact]
    public void ClickablePointIsNullForAnEmptyArray()
    {
        var empty = Create(VtR8, Array.Empty<double>());
        var three = Create(VtR8, [1.0, 2.0, 3.0]);
        try
        {
            Assert.Null(UiaValues.ToPoint(UiaValues.ReadDoubles(empty)));
            Assert.Null(UiaValues.ToPoint(UiaValues.ReadDoubles(three)));
        }
        finally
        {
            SafeArrayDestroy(empty);
            SafeArrayDestroy(three);
        }
    }

    private static SAFEARRAY* Create<T>(ushort type, T[] values) where T : unmanaged
    {
        var array = (SAFEARRAY*)SafeArrayCreateVector(type, 0, (uint)values.Length);
        Assert.True(array is not null, "SafeArrayCreateVector failed");
        void* data;
        Assert.Equal(0, SafeArrayAccessData(array, &data));
        values.CopyTo(new Span<T>(data, values.Length));
        Assert.Equal(0, SafeArrayUnaccessData(array));
        return array;
    }

    [LibraryImport("oleaut32.dll")]
    private static partial nint SafeArrayCreateVector(ushort type, int lowerBound, uint count);

    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayAccessData(SAFEARRAY* array, void** data);

    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayUnaccessData(SAFEARRAY* array);

    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayDestroy(SAFEARRAY* array);
}
