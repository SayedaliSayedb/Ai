using Aiagent.Models;
using Aiagent.Services;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace Aiagent.Hubs;

public class ChatHub : Hub
{
    private readonly ChatCoordinator _coordinator;
    private readonly ILogger<ChatHub> _logger;

    // ✅ اصلاح: قفل per-connection تا پیام‌های پشت سر هم یک کاربر
    // به‌صورت هم‌زمان وارد جریان استریم نشوند (قبل از این، اگر کاربر
    // پیام دوم را قبل از پایان استریم اول می‌فرستاد، دو حلقه‌ی خواندن
    // از کانال با هم قاطی می‌شدند).
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ConnectionLocks = new();

    public ChatHub(ChatCoordinator coordinator, ILogger<ChatHub> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    public async Task SendMessage(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            await Clients.Caller.SendAsync("ReceiveError", "پیام نمی‌تواند خالی باشد.");
            return;
        }

        userMessage = userMessage.Trim();
        var connectionId = Context.ConnectionId;

        var gate = ConnectionLocks.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(Context.ConnectionAborted);

        try
        {
            _logger.LogInformation("پیام از {ConnectionId}: {Message}", connectionId, userMessage);
            await Clients.Caller.SendAsync("ReceiveUserMessage", userMessage, Context.ConnectionAborted);

            // ✅ تشخیص پیام اول از پیام بعدی
            ConversationState? state;

            if (_coordinator.HasConversation(connectionId))
            {
                await _coordinator.SubmitFollowUpAsync(
                    connectionId, userMessage, Context.ConnectionAborted);
                state = _coordinator.GetConversation(connectionId);
            }
            else
            {
                state = await _coordinator.SubmitPromptAsync(
                    connectionId, userMessage, null, Context.ConnectionAborted);
            }

            if (state == null)
            {
                await Clients.Caller.SendAsync("ReceiveError", "مکالمه یافت نشد.");
                return;
            }

            // ✅ خواندن توکن‌ها از کانال
            try
            {
                await foreach (var token in state.TokenChannel.Reader.ReadAllAsync(Context.ConnectionAborted))
                {
                    if (Context.ConnectionAborted.IsCancellationRequested) break;
                    await Clients.Caller.SendAsync("ReceiveBotChunk", token, Context.ConnectionAborted);
                }
            }
            catch (OperationCanceledException) { }

            if (!Context.ConnectionAborted.IsCancellationRequested)
                await Clients.Caller.SendAsync("StreamComplete", Context.ConnectionAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "خطا در {ConnectionId}", connectionId);
            if (!Context.ConnectionAborted.IsCancellationRequested)
            {
                try
                {
                    await Clients.Caller.SendAsync(
                        "ReceiveError", $"خطا: {ex.Message}", Context.ConnectionAborted);
                }
                catch { }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _coordinator.RemoveConversation(Context.ConnectionId);
        if (ConnectionLocks.TryRemove(Context.ConnectionId, out var gate))
            gate.Dispose();
        await base.OnDisconnectedAsync(exception);
    }
}