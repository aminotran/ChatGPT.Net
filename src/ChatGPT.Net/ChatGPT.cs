﻿using ChatGPT.Net.DTO.ChatGPT;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ChatGPT.Net;

public class ChatGpt
{
    public Guid SessionId { get; set; }
    public ChatGptOptions Config { get; set; } = new();
    public List<ChatGptConversation> Conversations { get; set; } = new();
    public string APIKey { get; set; }
    /// <summary>
    /// <para>Allows adjusting httpclient timeout for arbitrary long or short content</para>
    /// <para>Default 100 second</para>
    /// </summary>
    public static int MaxTimeout { get; set; }

    public ChatGpt(string apikey,
        ChatGptOptions? config = null,
        int maxTimeout = 100)
    {
        Config = config ?? new ChatGptOptions();
        SessionId = Guid.NewGuid();
        APIKey = apikey;
        MaxTimeout = maxTimeout;
    }

    /// <summary>
    /// <para>Each HttpClient initialization will take up additional ports and will not immediately release itself when the program stops, leading to resource waste.</para>
    /// <para>https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines</para>
    /// </summary>
    private static HttpClient _httpClient = null;
    public static HttpClient httpClient
    {
        get
        {
            if (_httpClient == null)
            {
                _httpClient = new()
                {
                    Timeout = TimeSpan.FromSeconds(MaxTimeout)
                };
            }

            return _httpClient;
        }
    }

    private async IAsyncEnumerable<string> StreamCompletion(Stream stream)
    {
        using StreamReader reader = new(stream);
        while (!reader.EndOfStream)
        {
            string? line = await reader.ReadLineAsync();
            if (line != null)
            {
                yield return line;
            }
        }
    }

    public void SetConversationSystemMessage(string conversationId, string message)
    {
        ChatGptConversation conversation = GetConversation(conversationId);
        conversation.Messages.Add(new ChatGptMessage
        {
            Role = "system",
            Content = message
        });
    }

    public void ReplaceConversationSystemMessage(string conversationId, string message)
    {
        ChatGptConversation conversation = GetConversation(conversationId);
        conversation.Messages = conversation.Messages.Where(x => x.Role != "system").ToList();
        conversation.Messages.Add(new ChatGptMessage
        {
            Role = "system",
            Content = message
        });
    }

    public void RemoveConversationSystemMessages(string conversationId, string message)
    {
        ChatGptConversation conversation = GetConversation(conversationId);
        conversation.Messages = conversation.Messages.Where(x => x.Role != "system").ToList();
    }

    public List<ChatGptConversation> GetConversations()
    {
        return Conversations;
    }

    public void SetConversations(List<ChatGptConversation> conversations)
    {
        Conversations = conversations;
    }

    public ChatGptConversation GetConversation(string? conversationId)
    {
        if (conversationId is null)
        {
            return new ChatGptConversation();
        }

        ChatGptConversation? conversation = Conversations.FirstOrDefault(x => x.Id == conversationId);

        if (conversation != null) return conversation;
        conversation = new ChatGptConversation()
        {
            Id = conversationId
        };
        Conversations.Add(conversation);

        return conversation;
    }

    public void SetConversation(string conversationId, ChatGptConversation conversation)
    {
        ChatGptConversation? conv = Conversations.FirstOrDefault(x => x.Id == conversationId);

        if (conv != null)
        {
            conv = conversation;
        }
        else
        {
            Conversations.Add(conversation);
        }
    }

    public void RemoveConversation(string conversationId)
    {
        ChatGptConversation? conversation = Conversations.FirstOrDefault(x => x.Id == conversationId);

        if (conversation != null)
        {
            Conversations.Remove(conversation);
        }
    }

    public void ResetConversation(string conversationId)
    {
        ChatGptConversation? conversation = Conversations.FirstOrDefault(x => x.Id == conversationId);

        if (conversation == null) return;
        conversation.Messages = new();
    }

    public void ClearConversations()
    {
        Conversations.Clear();
    }

    public async Task<string> Ask(string prompt, string? conversationId = null)
    {
        ChatGptConversation conversation = GetConversation(conversationId);

        conversation.Messages.Add(new ChatGptMessage
        {
            Role = "user",
            Content = prompt
        });

        ChatGptResponse reply = await SendMessage(new ChatGptRequest
        {
            Messages = conversation.Messages,
            Model = Config.Model,
            Stream = false,
            Temperature = Config.Temperature,
            TopP = Config.TopP,
            FrequencyPenalty = Config.FrequencyPenalty,
            PresencePenalty = Config.PresencePenalty,
            Stop = Config.Stop,
            MaxTokens = Config.MaxTokens,
        });

        conversation.Updated = DateTime.Now;

        string response = reply.Choices.FirstOrDefault()?.Message.Content ?? "";

        conversation.Messages.Add(new ChatGptMessage
        {
            Role = "assistant",
            Content = response
        });

        return response;
    }

    public async Task<string> AskStream(Action<string> callback, string prompt, string? conversationId = null)
    {
        ChatGptConversation conversation = GetConversation(conversationId);

        conversation.Messages.Add(new ChatGptMessage
        {
            Role = "user",
            Content = prompt
        });

        ChatGptResponse reply = await SendMessage(new ChatGptRequest
        {
            Messages = conversation.Messages,
            Model = Config.Model,
            Stream = true,
            Temperature = Config.Temperature,
            TopP = Config.TopP,
            FrequencyPenalty = Config.FrequencyPenalty,
            PresencePenalty = Config.PresencePenalty,
            Stop = Config.Stop,
            MaxTokens = Config.MaxTokens,
        }, response =>
        {
            string? content = response.Choices.FirstOrDefault()?.Delta.Content;
            if (content is null) return;
            if (!string.IsNullOrWhiteSpace(content)) callback(content);
        });

        conversation.Updated = DateTime.Now;

        return reply.Choices.FirstOrDefault()?.Message.Content ?? "";
    }

    public async Task<ChatGptResponse> SendMessage(ChatGptRequest requestBody, Action<ChatGptStreamChunkResponse>? callback = null)
    {
        HttpClient client = ChatGpt.httpClient;
        HttpRequestMessage request = new()
        {
            Method = HttpMethod.Post,
            RequestUri = new Uri($"{Config.BaseUrl}/v1/chat/completions"),
            Headers =
            {
                {"Authorization", $"Bearer {APIKey}" }
            },
            Content = new StringContent(JsonConvert.SerializeObject(requestBody))
            {
                Headers =
                {
                    ContentType = new MediaTypeHeaderValue("application/json")
                }
            }
        };

        HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        if (requestBody.Stream)
        {
            string? contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType != "text/event-stream")
            {
                ChatGptResponse? error = await response.Content.ReadFromJsonAsync<ChatGptResponse>();
                throw new Exception(error?.Error?.Message ?? "Unknown error");
            }

            string concatMessages = string.Empty;

            ChatGptStreamChunkResponse? reply = null;
            Stream stream = await response.Content.ReadAsStreamAsync();
            await foreach (string data in StreamCompletion(stream))
            {
                string jsonString = data.Replace("data: ", "");
                if (string.IsNullOrWhiteSpace(jsonString)) continue;
                if (jsonString == "[DONE]") break;
                reply = JsonConvert.DeserializeObject<ChatGptStreamChunkResponse>(jsonString);
                if (reply is null) continue;
                concatMessages += reply.Choices.FirstOrDefault()?.Delta.Content;
                callback?.Invoke(reply);
            }

            return new ChatGptResponse
            {
                Id = reply?.Id ?? Guid.NewGuid().ToString(),
                Model = reply?.Model ?? "gpt-3.5-turbo",
                Created = reply?.Created ?? 0,
                Choices = new List<Choice>
                {
                    new()
                    {
                        Message = new ChatGptMessage
                        {
                            Content = concatMessages
                        }
                    }
                }
            };
        }

        ChatGptResponse? content = await response.Content.ReadFromJsonAsync<ChatGptResponse>();
        if (content is null) throw new Exception("Unknown error");
        if (content.Error is not null) throw new Exception(content.Error.Message);
        return content;
    }
}
