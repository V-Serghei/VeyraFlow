namespace Veyra.Application.DTOs.Auth;

public enum AccessTokenValidityState
{
    Missing = 0,
    Invalid = 1,
    Expired = 2,
    ExpiringSoon = 3,
    Valid = 4
}
