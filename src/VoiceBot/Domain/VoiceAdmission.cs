namespace VoiceBot.Domain;

public enum AdmissionResult
{
    Accepted,
    TooLong,
    TooBig,
    UserBusy,
    QueueFull
}

public static class VoiceAdmission
{
    public static AdmissionResult Check(
        int durationSeconds,
        long? fileBytes,
        int pendingForUser,
        bool queueHasSpace,
        LimitOptions limits)
    {
        if (durationSeconds > limits.MaxDurationSeconds)
            return AdmissionResult.TooLong;
        if (fileBytes > limits.MaxFileBytes)
            return AdmissionResult.TooBig;
        if (pendingForUser >= limits.MaxPendingPerUser)
            return AdmissionResult.UserBusy;
        if (!queueHasSpace)
            return AdmissionResult.QueueFull;
        return AdmissionResult.Accepted;
    }
}
