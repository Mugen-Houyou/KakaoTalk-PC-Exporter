# REST API Usage Guide

This document describes the lightweight REST interface exposed by **KakaoTalk PC Exporter**. The API is designed for local automation and integrations that need to fetch chat history stored in the application's SQLite database.

## Service lifecycle

- The API service starts automatically when the main WPF application launches. A log entry like `[REST] 서비스가 시작되었습니다.` is appended to the in-app log when the listener is ready.
- The service is automatically disposed when the main window is closed. No additional action is required from users.
- By default the listener loads with `RestApi:AllowAnyHost = true`, so the active prefix is `http://+:5010/` (strong wildcard). This allows inbound requests on port `5010` from any hostname on the machine. To restrict the binding, disable `AllowAnyHost` and provide explicit hostnames via `RestApi:Host` or `RestApi:Hosts`. Set `RestApi:UseHttps` to `true` once the appropriate HTTPS certificate binding has been configured on the machine.
- Multiple hostnames can be registered simultaneously by either providing a string array under `RestApi:Hosts` or by separating values with commas in `RestApi:Host` (for example: `"localhost", "192.168.0.123", "mytesthost.com"`). Each hostname may optionally include a custom port (e.g. `"mytesthost.com:8080"`); if omitted, the shared `RestApi:Port` value is used.
- Flash 캡처가 새로운 메시지를 저장할 때 애플리케이션은 기본적으로 `http://localhost:8080/webhook/message-update` 엔드포인트로 Webhook 알림을 발송한다. 다른 서버로 전달하고 싶다면 `appsettings.json`의 `Webhook:RemoteHost`, `Webhook:Prefix`, `Webhook:MessageUpdateUrl` 값을 조합하여 수정하면 된다. 헬스체크 엔드포인트는 `Webhook:HealthCheck`로 지정한다.

> **Note:** The service uses the built-in `HttpListener` class. Running behind a firewall or on a restricted network may require granting URL ACL permissions for the chosen prefix. 자세한 가이드는 [`HttpListener_URLACL.md`](HttpListener_URLACL.md)를 참고하세요.

## Authentication

The API is intended for local trusted use and does not implement authentication. Do **not** expose the listener to untrusted networks.

## Endpoints

### `GET /messages/{chatTitle}`

Fetches every stored message for the chat room whose title matches `{chatTitle}` exactly (case-sensitive, as stored in the `chats` table).

#### Request

```
GET http://localhost:5010/messages/%EC%B1%84%ED%8C%85%EB%B0%A9%EC%9D%B4%EB%A6%84%EC%98%88%EC%8B%9C
Accept: application/json
```

- URL-encode the chat title when sending the request.
- Only the `GET` method is supported. Other HTTP verbs return **405 Method Not Allowed**.
- If the chat room exists but has no stored rows, the endpoint responds with an empty JSON array (`[]`).

#### Successful response

```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

[
  {
    "chat_room": "채팅방이름예시",
    "sender": "김민석",
    "timestamp": "2025-09-26 08:14:00",
    "order": "0",
    "content": "안녕하세요 저는 김민석입니다."
  },
  {
    "chat_room": "채팅방이름예시",
    "sender": "김성원",
    "timestamp": "2025-09-26 08:15:00",
    "order": "4",
    "content": "안녕하세요 저는 김성원입니다."
  }
]
```

- Messages are returned in ascending `msg_order` (and `id` as a tie-breaker) to preserve the original conversation order.
- `timestamp` is formatted as `yyyy-MM-dd HH:mm:ss` when the stored value can be parsed. Otherwise the raw database value is returned.
- `order` represents the numeric `msg_order` column, serialized as a string to match the historical export format.

#### Error responses

| Status | Condition | Body |
| ------ | --------- | ---- |
| 400 Bad Request | Missing or empty `chatTitle` | `{ "message": "Chat room title is required." }` |
| 404 Not Found | Chat room title not found in the `chats` table | `{ "message": "Chat room not found." }` |
| 404 Not Found | Any other URL | `{ "message": "Endpoint not found." }` |
| 405 Method Not Allowed | HTTP verb other than `GET` | `{ "message": "Only GET is supported." }` |
| 500 Internal Server Error | Database or unexpected runtime error | `{ "message": "Failed to read messages." }` or `{ "message": "Internal server error." }` |

Error payloads share the same structure: a JSON object with a single `message` property describing the issue.

## Example usage (PowerShell)

```powershell
# Replace with the exact chat title you want to fetch
$chatTitle = "채팅방이름예시"
$encodedTitle = [uri]::EscapeDataString($chatTitle)

Invoke-RestMethod "http://localhost:5010/messages/$encodedTitle"
```

### `GET /api/webhook/health`

Simple readiness probe for webhook receivers.

#### Request

```
GET http://localhost:5010/api/webhook/health
Accept: text/plain
```

#### Response

```
HTTP/1.1 200 OK
Content-Type: text/plain; charset=utf-8

success
```

- Useful for monitoring whether the REST listener is reachable from another service.
- Responds to `GET` only; other methods are rejected with **405 Method Not Allowed** just like the `/messages` endpoint.

## Troubleshooting

- **Port already in use:** Ensure nothing else is bound to port `5010`, or update `RestApi:Port` in `appsettings.json`.
- **Access denied when starting the listener:** Run the application with administrator privileges or register the URL ACL via `netsh http add urlacl url=http://+:5010/ user=DOMAIN\\User`.
- **Empty responses:** Confirm the target chat has been captured and stored in the SQLite database located at `data/kakao_chat_v2.db`.

## Extending the API

To add more endpoints, follow the existing pattern inside `RestApiService.ProcessRequestAsync`. Match the first URL segment, validate inputs, query the database through `Microsoft.Data.Sqlite`, and respond with UTF-8 JSON payloads using the shared serialization options.

## Webhook notifications

FLASH 방식 캡처로 DB에 새 메시지가 저장되면 각 메시지에 대해 `Webhook:RemoteHost` + `Webhook:Prefix` + `Webhook:MessageUpdateUrl`로 POST 요청이 전송된다. 기본값은 `http://localhost:8080/webhook/message-update`이며 다음과 같은 JSON 페이로드를 사용한다.

```
POST /api/webhook/message-update
Content-Type: application/json; charset=utf-8

{
  "host": "mytesthost123",
  "chatRoom": "박주영",
  "sender": "박주영",
  "timestamp": "2025-09-29 10:30:00",
  "order": 1,
  "content": "새로운 메시지"
}
```

`host` 필드는 `appsettings.json`의 `ExporterHostname` 값을 사용하며 기본값은 `mytesthost123`이다. 타임스탬프는 `yyyy-MM-dd HH:mm:ss` 형식으로 직렬화되며, `order`는 `msg_order` 값을 그대로 전달한다. 다른 엔드포인트로 전달하려면 `appsettings.json`의 `Webhook:RemoteHost`, `Webhook:Prefix`, `Webhook:MessageUpdateUrl` 값을 원하는 값으로 조합해 수정하면 된다.
