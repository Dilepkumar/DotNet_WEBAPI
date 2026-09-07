namespace RoomLedger.Domain.Common;

public enum MemberRole { Admin = 1, Member = 2 }
public enum MemberStatus { Active = 1, Left = 2, Removed = 3 }
public enum OtpPurpose { Registration = 1, Login = 2, PasswordReset = 3 }
public enum ContributionStatus { Pending = 0, Approved = 1, Rejected = 2 }
