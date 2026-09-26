using Bridge.Core.Archives;
using Bridge.Core.Batches;
using Bridge.Core.Config;
using Bridge.Core.LlmSocial;
using Bridge.Core.Naming;
using Bridge.Core.Records;

namespace Bridge.Core.Delivery;

/// <summary>Asked once per group chat whose name is neither in the batch nor remembered.</summary>
public sealed record ChatNameRequest(string FileName, IReadOnlyList<string> Senders, int MessageCount);

public sealed record ChatNameAnswer(string ChatName, bool Remember);

public sealed record FileOutcome(
    string FileName,
    string? ChatName,
    int MessageCount,
    int Sent,
    string Status,
    string Detail,
    bool LastFromSelf);

public sealed record BatchOutcome(BatchId Id, string Target, IReadOnlyList<FileOutcome> Files)
{
    /// <summary>Nothing left to retry: every file was delivered, partially delivered, or had nothing to send.</summary>
    public bool Succeeded => Files.Count > 0 && Files.All(f => f.Status is RecordStatus.Sent or RecordStatus.Partial or RecordStatus.Empty or RecordStatus.Copied);

    public int Sent => Files.Sum(f => f.Sent);

    public bool AnyLastFromSelf => Files.Any(f => f.Status == RecordStatus.Sent && f.LastFromSelf);
}

/// <summary>
/// One batch, start to finish: read each ZIP, work out who the conversation is with, post to llmsocial,
/// write a record, and move the batch to done\ or failed\. Retrying a failed batch re-sends everything,
/// which is safe because llmsocial answers 200 to a message id it already has.
/// </summary>
public sealed class LlmSocialDelivery
{
    public const string TargetName = "llmsocial";

    private readonly LlmSocialClient _client;
    private readonly IReadOnlyCollection<string> _myNames;
    private readonly GroupMemoryStore _groups;
    private readonly RecordsLog _records;
    private readonly BatchInbox _inbox;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _timeZone;

    public LlmSocialDelivery(
        LlmSocialClient client,
        IReadOnlyCollection<string> myNames,
        GroupMemoryStore groups,
        RecordsLog records,
        BatchInbox inbox,
        TimeProvider clock,
        TimeZoneInfo timeZone)
    {
        _client = client;
        _myNames = myNames;
        _groups = groups;
        _records = records;
        _inbox = inbox;
        _clock = clock;
        _timeZone = timeZone;
    }

    public async Task<BatchOutcome> DeliverAsync(ReadyBatch batch, Func<ChatNameRequest, Task<ChatNameAnswer?>> askChatName, CancellationToken ct = default)
    {
        var outcomes = new List<FileOutcome>();
        foreach (var path in batch.FilePaths)
        {
            var outcome = await DeliverFileAsync(batch, path, askChatName, ct);
            outcomes.Add(outcome);
            _records.Append(new DeliveryRecord(
                $"{batch.Id.Value}/{outcomes.Count}",
                _clock.GetUtcNow(),
                batch.Id.Value,
                outcome.ChatName,
                outcome.MessageCount,
                TargetName,
                outcome.Status,
                outcome.Detail));
        }

        var result = new BatchOutcome(batch.Id, TargetName, outcomes);
        if (result.Succeeded)
        {
            _inbox.MarkDone(batch.Id);
        }
        else
        {
            var reason = string.Join("\n", outcomes.Where(o => o.Status is RecordStatus.Failed or RecordStatus.Cancelled).Select(o => $"{o.FileName}：{o.Detail}"));
            _inbox.MarkFailed(batch.Id, reason);
        }

        return result;
    }

    private async Task<FileOutcome> DeliverFileAsync(ReadyBatch batch, string path, Func<ChatNameRequest, Task<ChatNameAnswer?>> askChatName, CancellationToken ct)
    {
        var fileName = Path.GetFileName(path);
        ArchiveTranscript archive;
        try
        {
            archive = NativeArchive.ReadTranscript(path);
        }
        catch (ArchiveException ex)
        {
            return new FileOutcome(fileName, null, 0, 0, RecordStatus.Failed, ex.Message, false);
        }

        var transcript = archive.Transcript;
        var chatName = batch.Manifest.ChatName ?? DisplayName.ChatNameFromArchiveName(fileName);
        var mapping = PayloadMapper.Map(transcript, chatName, _myNames, _timeZone);

        if (mapping.NeedsChatName)
        {
            var remembered = _groups.Match(mapping.OtherSenders);
            if (remembered is null)
            {
                var answer = await askChatName(new ChatNameRequest(fileName, mapping.OtherSenders, transcript.Messages.Count));
                if (answer is null || answer.ChatName.Trim().Length == 0)
                {
                    return new FileOutcome(fileName, null, transcript.Messages.Count, 0, RecordStatus.Cancelled, "没有填群名，这段群聊没有发送。在记录里可以重试。", false);
                }

                chatName = answer.ChatName.Trim();
                if (answer.Remember)
                {
                    _groups.Remember(chatName, mapping.OtherSenders, _clock.GetUtcNow());
                }
            }
            else
            {
                chatName = remembered;
                _groups.Remember(chatName, mapping.OtherSenders, _clock.GetUtcNow());
            }

            mapping = PayloadMapper.Map(transcript, chatName, _myNames, _timeZone);
        }

        var name = mapping.IsGroup ? chatName : (chatName ?? mapping.Counterpart);
        if (mapping.Messages.Count == 0)
        {
            var why = mapping.OtherSenders.Count == 0 ? "这段记录里只有你自己的消息，没有对方的消息可发。" : "这段记录里没有可发的消息（正文都是空的）。";
            return new FileOutcome(fileName, name, transcript.Messages.Count, 0, RecordStatus.Empty, why, false);
        }

        var delivery = await _client.DeliverAsync(mapping.Messages, ct);
        var lastFromSelf = mapping.Messages[^1].FromSelf;
        var skippedNote = mapping.Skipped == 0 ? "" : $"，{mapping.Skipped} 条空消息略过";
        if (!delivery.Complete)
        {
            var status = delivery.Sent == 0 ? RecordStatus.Failed : RecordStatus.Partial;
            var detail = delivery.Sent == 0 ? delivery.AbortReason! : $"发出 {delivery.Sent}/{mapping.Messages.Count} 条后中止：{delivery.AbortReason}";
            return new FileOutcome(fileName, name, mapping.Messages.Count, delivery.Sent, status, detail, lastFromSelf);
        }

        if (delivery.Failures.Count > 0)
        {
            var detail = $"发出 {delivery.Sent}/{mapping.Messages.Count} 条，{delivery.Failures.Count} 条被拒：{delivery.Failures[0].Error}{skippedNote}";
            return new FileOutcome(fileName, name, mapping.Messages.Count, delivery.Sent, RecordStatus.Partial, detail, lastFromSelf);
        }

        var summary = $"已发 {delivery.Sent} 条给 llmsocial{skippedNote}" + (lastFromSelf ? "。最后一条是你发的，llmsocial 不会起草回复。" : "");
        return new FileOutcome(fileName, name, mapping.Messages.Count, delivery.Sent, RecordStatus.Sent, summary, lastFromSelf);
    }
}
