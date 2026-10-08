using RoomLedger.Application.Common.Models;
using RoomLedger.Domain.Entities;

namespace RoomLedger.Application.Common.Interfaces;

public interface IElectricityBillProvider
{
    Task<NormalizedElectricityBillResult> FetchBillAsync(
        ElectricityAccount account,
        CancellationToken cancellationToken = default);
}
