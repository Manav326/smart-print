namespace SmartPrint.Api.Modules.ShopDevices.Contracts;

public sealed record PrinterCreatedResponse(
    Guid Id,
    string DisplayName,
    string Manufacturer,
    string Model,
    string ConnectionType,
    string OutputMode,
    string? ConnectionAddress,
    string? QueueName,
    string CapabilitiesJson,
    string Status,
    bool IsActive);

public sealed record PrinterActivationResponse(Guid Id, bool IsActive, DateTimeOffset UpdatedUtc);

public sealed record QrStationCreatedResponse(
    Guid Id,
    string DisplayName,
    Guid? PrinterDeviceId,
    string PublicCodeHint,
    bool IsEnabled,
    string PublicUrl);

public sealed record QrStationCodeResponse(Guid Id, string PublicCodeHint, string PublicUrl);

public sealed record QrStationEnabledResponse(Guid Id, bool IsEnabled);

public enum ShopDeviceOperationKind
{
    Success,
    InvalidRequest,
    PortalNotConfigured,
    NotFound,
    Conflict,
    InvalidPrinterSelection
}

public sealed record ShopDeviceOperationResult<T>(
    ShopDeviceOperationKind Kind,
    T? Value = default,
    Dictionary<string, string[]>? ValidationErrors = null,
    string? Code = null,
    string? Message = null,
    long Current = 0,
    long? Maximum = null,
    string? ActiveJobNumber = null)
{
    public static ShopDeviceOperationResult<T> Success(T value) =>
        new(ShopDeviceOperationKind.Success, value);

    public static ShopDeviceOperationResult<T> Invalid(Dictionary<string, string[]> errors) =>
        new(ShopDeviceOperationKind.InvalidRequest, ValidationErrors: errors);

    public static ShopDeviceOperationResult<T> PortalNotConfigured() =>
        new(ShopDeviceOperationKind.PortalNotConfigured);

    public static ShopDeviceOperationResult<T> NotFound() =>
        new(ShopDeviceOperationKind.NotFound);

    public static ShopDeviceOperationResult<T> Conflict(string code, string message,
        long current = 0, long? maximum = null) =>
        new(ShopDeviceOperationKind.Conflict, Code: code, Message: message, Current: current, Maximum: maximum);

    public static ShopDeviceOperationResult<T> InvalidPrinterSelection(Dictionary<string, string[]> errors) =>
        new(ShopDeviceOperationKind.InvalidPrinterSelection, ValidationErrors: errors);

    public static ShopDeviceOperationResult<T> PrinterHasActiveOutput(string jobNumber) =>
        new(ShopDeviceOperationKind.Conflict,
            Code: "printer_has_active_output",
            Message: $"Printer cannot be disabled while job {jobNumber} is active in the Windows Agent or spooler. Resolve that same job first; disabling it does not cancel Windows output.",
            ActiveJobNumber: jobNumber);
}
