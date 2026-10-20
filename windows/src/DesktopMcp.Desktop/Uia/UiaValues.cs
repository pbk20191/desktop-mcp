using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
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

    /// <summary>Reads a one-dimensional SAFEARRAY of VT_I4 without taking ownership.</summary>
    public static int[] ReadInts(SAFEARRAY* array) => Read<int>(array);

    /// <summary>Reads a one-dimensional SAFEARRAY of VT_R8 without taking ownership.</summary>
    public static double[] ReadDoubles(SAFEARRAY* array) => Read<double>(array);

    private static T[] Read<T>(SAFEARRAY* array) where T : unmanaged
    {
        if (array is null) return [];
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
