using System.Runtime.InteropServices;
using DesktopMcp.Core.Snapshots;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Accessibility;

namespace DesktopMcp.Desktop.Uia;

/// <summary>
/// Turns UIA's BSTRs, VARIANTs and SAFEARRAYs into plain values and frees them (spec §6: every BSTR is freed and every
/// ComVariant disposed before the wrapper returns). RECTs go through <c>Win32Geometry.ToScreenRect</c>, the one
/// conversion. COM objects are not released here: the generated wrappers are released by their finalizers (see
/// <see cref="UiaWorker{TSession}"/>).
/// </summary>
internal static unsafe class UiaValues
{
    /// <summary>Copies a BSTR the callee allocated and frees it; null becomes "".</summary>
    public static string Take(BSTR value)
    {
        try { return value.ToString() ?? ""; }
        finally { PInvoke.SysFreeString(value); }
    }

    /// <summary>A cached VT_I4 property (enums such as the LegacyIAccessible role and the toggle state), else null.</summary>
    public static int? CachedInt(IUIAutomationElement element, UIA_PROPERTY_ID property)
    {
        using var value = element.GetCachedPropertyValue(property);
        return value.VarType == VarEnum.VT_I4 ? value.As<int>() : null;
    }

    /// <summary>A cached VT_R8 property (scroll percentages, range values), else null.</summary>
    public static double? CachedDouble(IUIAutomationElement element, UIA_PROPERTY_ID property)
    {
        using var value = element.GetCachedPropertyValue(property);
        return value.VarType switch
        {
            VarEnum.VT_R8 => value.As<double>(),
            VarEnum.VT_I4 => value.As<int>(),
            _ => null,
        };
    }

    /// <summary>A cached VT_BOOL property, else null.</summary>
    public static bool? CachedBool(IUIAutomationElement element, UIA_PROPERTY_ID property)
    {
        using var value = element.GetCachedPropertyValue(property);
        return value.VarType == VarEnum.VT_BOOL ? value.As<bool>() : null;
    }

    /// <summary>A cached VT_BSTR property, else null (the "not supported" sentinel is a VT_UNKNOWN).</summary>
    public static string? CachedString(IUIAutomationElement element, UIA_PROPERTY_ID property)
    {
        using var value = element.GetCachedPropertyValue(property);
        return value.VarType == VarEnum.VT_BSTR ? value.As<string>() : null;
    }

    /// <summary>The cached RuntimeId as "a.b.c" (the cycle-guard key), or null when the provider gave none.</summary>
    public static string? CachedRuntimeId(IUIAutomationElement element)
    {
        using var value = element.GetCachedPropertyValue(UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId);
        if (value.VarType != (VarEnum.VT_ARRAY | VarEnum.VT_I4)) return null;
        var parts = ReadInts((SAFEARRAY*)value.GetRawDataRef<nint>());
        return parts.Length == 0 ? null : string.Join('.', parts);
    }

    /// <summary>
    /// A cached point property (ClickablePoint): a two-element VT_ARRAY | VT_R8 of x and y, rounded half-even, else null
    /// (the "not supported" sentinel, an empty array, or any other shape).
    /// </summary>
    public static ScreenPoint? CachedPoint(IUIAutomationElement element, UIA_PROPERTY_ID id)
    {
        using var value = element.GetCachedPropertyValue(id);
        if (value.VarType != (VarEnum.VT_ARRAY | VarEnum.VT_R8)) return null;
        return ToPoint(ReadDoubles((SAFEARRAY*)value.GetRawDataRef<nint>()));
    }

    /// <summary>x and y rounded half-even from exactly two doubles; null for any other count or a non-finite value.</summary>
    internal static ScreenPoint? ToPoint(double[] values)
    {
        if (values.Length != 2 || !double.IsFinite(values[0]) || !double.IsFinite(values[1])) return null;
        if (Math.Abs(values[0]) > int.MaxValue || Math.Abs(values[1]) > int.MaxValue) return null;
        return new ScreenPoint((int)Math.Round(values[0], MidpointRounding.ToEven), (int)Math.Round(values[1], MidpointRounding.ToEven));
    }

    /// <summary>Reads a one-dimensional SAFEARRAY of VT_I4 without taking ownership; any other array reads as empty.</summary>
    public static int[] ReadInts(SAFEARRAY* array) => Read<int>(array, VARENUM.VT_I4);

    /// <summary>Reads a one-dimensional SAFEARRAY of VT_R8 without taking ownership; any other array reads as empty.</summary>
    public static double[] ReadDoubles(SAFEARRAY* array) => Read<double>(array, VARENUM.VT_R8);

    private static T[] Read<T>(SAFEARRAY* array, VARENUM type) where T : unmanaged
    {
        if (array is null) return [];
        // The array comes from a provider in another process: its shape is checked before its data is read, so a wrong
        // element type or size cannot make the copy run past the end of the buffer.
        if (PInvoke.SafeArrayGetDim(array) != 1 || PInvoke.SafeArrayGetElemsize(array) != (uint)sizeof(T)) return [];
        if (PInvoke.SafeArrayGetVartype(array, out var actual).Failed || actual != type) return [];
        PInvoke.SafeArrayGetLBound(array, 1, out var lower).ThrowOnFailure();
        PInvoke.SafeArrayGetUBound(array, 1, out var upper).ThrowOnFailure();
        var count = upper - lower + 1;
        if (count <= 0) return [];
        void* data;
        PInvoke.SafeArrayAccessData(array, &data).ThrowOnFailure();
        try { return new ReadOnlySpan<T>(data, count).ToArray(); }
        finally { PInvoke.SafeArrayUnaccessData(array); }
    }
}
