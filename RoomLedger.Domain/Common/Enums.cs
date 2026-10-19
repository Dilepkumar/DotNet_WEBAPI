using System.Text.Json.Serialization;

namespace RoomLedger.Domain.Common;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MemberRole { Admin = 1, Member = 2 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MemberStatus { Active = 1, Left = 2, Removed = 3 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OtpPurpose { Registration = 1, Login = 2, PasswordReset = 3 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContributionStatus { Pending = 0, Approved = 1, Rejected = 2 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Gender { Male = 1, Female = 2, Other = 3 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentPurpose { PoolContribution = 1, BillSplit = 2, ElectricitySplit = 3, IouSettlement = 4 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentStatus { Pending = 0, Processing = 1, Success = 2, Failed = 3, Expired = 4, Cancelled = 5 }
