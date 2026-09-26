// Order entry DTOs (Tier A), Phase 6, PROVISIONAL: modelled on the reference clients, not on a captured request.
//   Requests:  Qluxzz avanza/avanza.py place_order, delete_order, edit_order          @ a6a18a94
//   Response:  Go SDK trading/types.go PlaceOrderResponse (= Delete/ModifyOrderResponse) @ 43f39025
// The owner captures a real web-app order before Phase 7; until then nothing sends these.
namespace QuantAnalyst.Avanza.Dto;

internal sealed class PlaceOrderRequestDto
{
    public const string Version = "order.place/2026-09-26-provisional";

    public required string AccountId { get; init; }

    public required string OrderbookId { get; init; }

    /// <summary>BUY or SELL.</summary>
    public required string Side { get; init; }

    /// <summary>NORMAL (R3 allows nothing else).</summary>
    public required string Condition { get; init; }

    public required decimal Price { get; init; }

    /// <summary>yyyy-MM-dd.</summary>
    public required string ValidUntil { get; init; }

    public required long Volume { get; init; }
}

internal sealed class DeleteOrderRequestDto
{
    public const string Version = "order.delete/2026-09-26-provisional";

    public required string AccountId { get; init; }

    public required string OrderId { get; init; }
}

internal sealed class ModifyOrderRequestDto
{
    public const string Version = "order.modify/2026-09-26-provisional";

    public required string AccountId { get; init; }

    public required OrderMetadataDto Metadata { get; init; }

    /// <summary>Sent as an explicit null, as the reference client does.</summary>
    public long? OpenVolume { get; init; }

    public required string OrderId { get; init; }

    public required decimal Price { get; init; }

    public required string ValidUntil { get; init; }

    public required long Volume { get; init; }
}

internal sealed class OrderMetadataDto
{
    public required string OrderEntryMode { get; init; }
}

/// <summary>The answer to place, delete and modify: {orderRequestStatus: SUCCESS|ERROR, message, parameters, orderId}.</summary>
internal sealed class OrderRequestResponseDto
{
    public const string Version = "order.response/2026-09-26-provisional";

    public required string OrderRequestStatus { get; init; }

    public string? Message { get; init; }

    public List<string>? Parameters { get; init; }

    public string? OrderId { get; init; }
}
