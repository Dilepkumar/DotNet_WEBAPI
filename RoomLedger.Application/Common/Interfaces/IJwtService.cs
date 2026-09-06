namespace RoomLedger.Application.Common.Interfaces;

public interface IJwtService
{
    string CreateToken(int userId, string email);
}
