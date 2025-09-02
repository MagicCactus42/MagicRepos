namespace MagicRepos.Protocol.Messages;

public enum MessageType : byte
{
    NegotiateRequest = 1,
    NegotiateResponse = 2,
    RefAdvertisement = 3,
    RefUpdate = 4,
    RefWanted = 5,
    PackData = 6,
    PackComplete = 7,
    Ok = 8,
    Error = 9,
    PrCreate = 20,
    PrList = 21,
    PrReview = 22,
    PrMerge = 23,
    PrResponse = 24,
}
