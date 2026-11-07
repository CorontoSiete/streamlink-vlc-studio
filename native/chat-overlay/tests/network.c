/* Exercise the production protocol readers with deterministic TLS bytes, no network. */
#define tls_recv test_tls_recv
#define tls_send test_tls_send
#define tls_connect test_tls_connect
#define tls_close test_tls_close
#define main overlay_controller_main
#include "../vlc_chat_overlay.c"
#undef main
#include <assert.h>
#include <io.h>

static const uint8_t *incoming;
static size_t incoming_size, incoming_offset, read_chunk;
static uint8_t outgoing[8192];
static size_t outgoing_size;
static bool fail_send, automatic_upgrade;
static const kick_pusher_connection_t test_connection = {"ws-eu.pusher.com", "/app/currentKey?protocol=7"};
static char upgrade_response[8192];
static const kick_pusher_connection_t *expected_handshake = &test_connection;
static const uint8_t *upgrade_frames;
static size_t upgrade_frames_size, established_frame_size, subscription_threshold;
static tls_conn_t *const connection = (tls_conn_t *)(uintptr_t)1;

static void set_input(const void *bytes, size_t length, size_t chunk) {
    incoming = bytes;
    incoming_size = length;
    incoming_offset = outgoing_size = 0;
    read_chunk = chunk;
    fail_send = automatic_upgrade = false;
    upgrade_frames = NULL;
    upgrade_frames_size = established_frame_size = subscription_threshold = 0;
    expected_handshake = &test_connection;
    g_stop = 0;
}

int test_tls_recv(tls_conn_t *tls, void *buffer, int size) {
    assert(tls == connection && size > 0);
    size_t count = incoming_size - incoming_offset;
    if (count > (size_t)size) count = (size_t)size;
    if (count > read_chunk) count = read_chunk;
    memcpy(buffer, incoming + incoming_offset, count);
    incoming_offset += count;
    return (int)count;
}

int test_tls_send(tls_conn_t *tls, const void *bytes, int size) {
    assert(tls == connection && size >= 0);
    if (fail_send) return -1;
    assert(outgoing_size + (size_t)size <= sizeof(outgoing));
    memcpy(outgoing + outgoing_size, bytes, (size_t)size);
    outgoing_size += (size_t)size;
    if (automatic_upgrade) {
        char request[2048], key[25], accept[29];
        assert((size_t)size < sizeof(request));
        memcpy(request, bytes, (size_t)size);
        request[size] = '\0';
        char expected[512];
        snprintf(expected, sizeof(expected), "GET %s HTTP/1.1\r\n", expected_handshake->path);
        assert(strstr(request, expected));
        snprintf(expected, sizeof(expected), "Host: %s\r\n", expected_handshake->host);
        assert(strstr(request, expected));
        const char *value = strstr(request, "Sec-WebSocket-Key: ");
        assert(value);
        memcpy(key, value + strlen("Sec-WebSocket-Key: "), 24);
        key[24] = '\0';
        assert(make_websocket_accept(key, accept, sizeof(accept)));
        int count = snprintf(upgrade_response, sizeof(upgrade_response),
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            "Sec-WebSocket-Accept: %s\r\n\r\n", accept);
        assert(count > 0 && (size_t)count + 3 < sizeof(upgrade_response));
        if (upgrade_frames) {
            assert((size_t)count + upgrade_frames_size < sizeof(upgrade_response));
            memcpy(upgrade_response + count, upgrade_frames, upgrade_frames_size);
            subscription_threshold = (size_t)count + established_frame_size;
        } else {
            upgrade_response[count] = (char)0x81;
            upgrade_response[count + 1] = 1;
            upgrade_response[count + 2] = '!';
        }
        incoming = (const uint8_t *)upgrade_response;
        incoming_size = (size_t)count + (upgrade_frames ? upgrade_frames_size : 3);
        incoming_offset = 0;
        automatic_upgrade = false;
    }
    else if (subscription_threshold) {
        assert(incoming_offset >= subscription_threshold); /* Pusher acknowledgement precedes subscribe. */
    }
    return size;
}

tls_conn_t *test_tls_connect(const char *host, const char *port) {
    (void)host; (void)port;
    return connection;
}

void test_tls_close(tls_conn_t *tls) { assert(tls == connection); }

static void test_upgrade(void) {
    const char *key = "dGhlIHNhbXBsZSBub25jZQ==";
    char accept[29];
    assert(make_websocket_accept(key, accept, sizeof(accept)));
    assert(strcmp(accept, "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=") == 0);
    const char *valid = "HTTP/1.1 101 Switching Protocols\r\n"
        "upgrade: WebSocket\r\nConnection: keep-alive, Upgrade\r\n"
        "Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n";
    char response[4096];
    strcpy(response, valid);
    assert(websocket_validate_upgrade(response, key));
    set_input("", 0, 1);
    automatic_upgrade = true;
    assert(websocket_handshake(connection, &test_connection));
    assert(incoming_size - incoming_offset == 3);
    char text[4];
    assert(websocket_read_text(connection, text, sizeof(text)) && strcmp(text, "!") == 0);
    const char *invalid[] = {
        "HTTP/1.1 200 OK\r\nX-Note: 101 unused\r\n\r\n",
        "HTTP/1.1 101 Switching Protocols\r\n\r\n",
        "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            "Sec-WebSocket-Accept: wrong\r\n\r\n",
        "HTTP/1.1 1010 Wrong Status\r\n\r\n"
    };
    for (size_t i = 0; i < sizeof(invalid) / sizeof(invalid[0]); i++) {
        strcpy(response, invalid[i]);
        assert(!websocket_validate_upgrade(response, key));
        set_input(invalid[i], strlen(invalid[i]), 1);
        assert(!websocket_handshake(connection, &test_connection));
    }
    /* A 101 at the front of an unterminated oversized header used to pass. */
    memset(response, 'x', sizeof(response));
    memcpy(response, "HTTP/1.1 101 Switching Protocols\r\nX: ", 36);
    set_input(response, sizeof(response), 1);
    assert(!websocket_handshake(connection, &test_connection));
    set_input("", 0, 1);
    g_stop = 1;
    assert(!websocket_handshake(connection, &test_connection));
    assert(outgoing_size == 0);
    g_stop = 0;
    /* Duplicated accept and unsolicited extensions cannot negotiate a protocol. */
    const char *extra[] = {"Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=",
                          "Sec-WebSocket-Extensions: permessage-deflate",
                          "Sec-WebSocket-Protocol: unexpected"};
    for (size_t i = 0; i < sizeof(extra) / sizeof(extra[0]); i++) {
        snprintf(response, sizeof(response), "%.*s%s\r\n\r\n", (int)strlen(valid) - 2, valid, extra[i]);
        assert(!websocket_validate_upgrade(response, key));
    }
    puts("PASS WebSocket upgrade validates status, headers, accept key and cancellation");
}

static void test_text_messages(void) {
    /* A UTF-8 sequence spans three fragments, with ping/pong between them. */
    const uint8_t frames[] = {1, 2, 'a', 0xf0, 0x89, 2, 'h', 'i', 0,
        1, 0x9f, 0x8a, 0, 0x80, 3, 0x98, 0x80, 'z', 0x81, 1, '!'};
    for (size_t chunk = 1; chunk <= sizeof(frames); chunk++) {
        set_input(frames, sizeof(frames), chunk);
        char text[32];
        assert(websocket_read_text(connection, text, sizeof(text)));
        assert(strcmp(text, "a\xf0\x9f\x98\x80z") == 0);
        assert(outgoing_size == 8 && outgoing[0] == 0x8a && outgoing[1] == 0x82);
        assert((outgoing[6] ^ outgoing[2]) == 'h');
        assert((outgoing[7] ^ outgoing[3]) == 'i');
        assert(websocket_read_text(connection, text, sizeof(text)) && strcmp(text, "!") == 0);
    }
    const uint8_t empty_fragments[] = {1, 0, 0, 0, 0x80, 0};
    set_input(empty_fragments, sizeof(empty_fragments), 1);
    char text[32];
    assert(websocket_read_text(connection, text, sizeof(text)) && !text[0]);
    const uint8_t overflow[] = {1, 2, 'a', 'b', 0x80, 2, 'c', 'd'};
    set_input(overflow, sizeof(overflow), 1);
    assert(!websocket_read_text(connection, text, 4));
    set_input(overflow, sizeof(overflow), 1);
    assert(websocket_read_text(connection, text, 5) && strcmp(text, "abcd") == 0);
    const uint8_t ping[] = {0x89, 0};
    set_input(ping, sizeof(ping), 1);
    fail_send = true;
    assert(!websocket_read_text(connection, text, sizeof(text)));
    const uint8_t closed[] = {0x88, 8, 15, 163, 'r', 'e', 't', 'i', 'r', 'e'};
    set_input(closed, sizeof(closed), 2);
    assert(!websocket_read_text(connection, text, sizeof(text)));
    assert(g_kick_close_code == 4003 && strcmp(g_kick_close_reason, "retire") == 0);
    puts("PASS WebSocket text joins fragments across control frames and enforces the total bound");
}

static void test_invalid_frames(void) {
    const struct { uint8_t bytes[16]; size_t length; } cases[] = {
        {{0x80, 0}, 2},                       /* Orphan continuation. */
        {{1, 0, 0x81, 0}, 4},                /* New text during fragmentation. */
        {{0xc1, 0}, 2},                       /* Unnegotiated RSV. */
        {{0x81, 0x80, 0, 0, 0, 0}, 6},       /* Masked server frame. */
        {{0x83, 0}, 2},                       /* Reserved opcode. */
        {{9, 0}, 2},                          /* Fragmented control. */
        {{0x89, 126, 0, 126}, 4},             /* Oversized control. */
        {{0x81, 126, 0, 1, 'x'}, 5},          /* Nonminimal length. */
        {{0x81, 127, 0x80, 0, 0, 0, 0, 0, 0, 0}, 10},
        {{0x81, 127, 0, 0, 0, 0, 0, 0, 0, 1}, 10},
        {{0x81, 3, 'x'}, 3},                 /* Truncated payload. */
        {{0x81, 1, 0xff}, 3},                /* Invalid UTF-8. */
        {{0x81, 1, 0}, 3},                   /* NUL cannot enter a JSON C string. */
        {{1, 1, 'x', 0x88, 0}, 5},           /* Close in a partial message. */
        {{0x82, 0}, 2}                       /* Text-only application. */
    };
    for (size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); i++) {
        char text[32];
        set_input(cases[i].bytes, cases[i].length, 1);
        assert(!websocket_read_text(connection, text, sizeof(text)));
    }
    /* Exercise the extended length using a real, bounded message. */
    uint8_t large[130] = {0x81, 126, 0, 126};
    memset(large + 4, 'x', 126);
    char text[127];
    set_input(large, sizeof(large), 3);
    assert(websocket_read_text(connection, text, sizeof(text)) && strlen(text) == 126);
    puts("PASS WebSocket readers reject malformed frames, invalid text and truncated messages");
}

static void test_irc_overflow(void) {
    const char *suffix = ":viewer!viewer@host PRIVMSG #test :must not be parsed\r\n";
    uint8_t bytes[IRC_LINE_BUF + 128];
    memset(bytes, 'x', IRC_LINE_BUF - 1);
    strcpy((char *)bytes + IRC_LINE_BUF - 1, suffix);
    set_input(bytes, IRC_LINE_BUF - 1 + strlen(suffix), IRC_LINE_BUF - 1);
    queue_init(&g_queue);
    InitializeCriticalSection(&g_irc_send_cs);
    strcpy(g_channel, "test");
    irc_session();
    assert(incoming_offset == IRC_LINE_BUF - 1);
    for (int i = 0; i < g_queue.count; i++) assert(g_queue.buf[i].is_system);
    DeleteCriticalSection(&g_irc_send_cs);
    DeleteCriticalSection(&g_queue.cs);
    puts("PASS oversized IRC lines disconnect without parsing their suffix as a message");
}


static const char *negotiation_json;
static DWORD negotiation_status;
static int negotiation_requests;

static bool fake_connection_post(const wchar_t *host, const wchar_t *path, const wchar_t *headers,
                                  const char *body, size_t body_len, size_t max_bytes,
                                  DWORD *status, byte_buf_t *response) {
    assert(wcscmp(host, L"web.kick.com") == 0);
    assert(wcscmp(path, L"/api/v1/realtime/channels/42/chat/connection") == 0);
    assert(wcsstr(headers, L"Origin: https://kick.com\r\n"));
    assert(wcsstr(headers, L"x-app-platform: web\r\n"));
    assert(!wcsstr(headers, L"Authorization") && !wcsstr(headers, L"Cookie"));
    assert(body_len == strlen(body) && max_bytes == 65536);
    assert(strstr(body, "\"accepted_providers\":[{\"provider\":\"pusher\"}]"));
    assert(strstr(body, g_kick_realtime_client_id) && strlen(g_kick_realtime_client_id) == 36);
    assert(!strstr(body, "account-secret"));
    *status = negotiation_status;
    response->size = strlen(negotiation_json);
    response->data = (uint8_t *)strdup(negotiation_json);
    negotiation_requests++;
    return true;
}

static void test_kick_connection_negotiation(void) {
    strcpy(g_kick_channel_id, "42");
    strcpy(g_kick_chatroom_id, "9001"); /* These identities must not be interchanged. */
    strcpy(g_kick_chat_token, "account-secret");
    g_kick_realtime_client_id[0] = '\0';
    negotiation_status = 200;
    negotiation_json = "{\"data\":{\"mode\":\"websocket\",\"connections\":["
        "{\"provider\":\"pusher\",\"credentials\":{\"app_key\":\"newKey1\",\"cluster\":\"eu\"}}]}}";
    kick_pusher_connection_t config;
    g_stop = 0;
    assert(kick_negotiate_pusher_connection(&config, fake_connection_post));
    assert(strcmp(config.host, "ws-eu.pusher.com") == 0);
    assert(strstr(config.path, "/app/newKey1?protocol=7"));
    char identity[37];
    strcpy(identity, g_kick_realtime_client_id);
    negotiation_json = "{\"data\":{\"mode\":\"websocket\",\"connections\":["
        "{\"provider\":\"pusher\",\"credentials\":{\"app_key\":\"newKey2\",\"cluster\":\"us2\"}}]}}";
    assert(kick_negotiate_pusher_connection(&config, fake_connection_post));
    assert(strcmp(config.host, "ws-us2.pusher.com") == 0 && strstr(config.path, "/app/newKey2?"));
    assert(strcmp(identity, g_kick_realtime_client_id) == 0 && negotiation_requests == 2);
    negotiation_status = 503;
    assert(!kick_negotiate_pusher_connection(&config, fake_connection_post));
    negotiation_status = 200;
    const char *invalid[] = {
        "{}", "[]", "{\"data\":{\"mode\":\"polling\",\"connections\":[]}}",
        "{\"data\":{\"mode\":\"websocket\",\"connections\":[{\"provider\":\"centrifugo\"}]}}",
        "{\"data\":{\"mode\":\"websocket\",\"connections\":[{\"provider\":\"pusher\",\"credentials\":{\"app_key\":\"key\",\"cluster\":\"us2.attacker\"}}]}}",
        "{\"data\":{\"mode\":\"websocket\",\"connections\":[{\"provider\":\"pusher\",\"credentials\":{\"app_key\":\"bad/key\",\"cluster\":\"us2\"}}]}}"
    };
    for (size_t i = 0; i < sizeof(invalid) / sizeof(invalid[0]); i++) {
        negotiation_json = invalid[i];
        assert(!kick_negotiate_pusher_connection(&config, fake_connection_post));
    }
    int requests = negotiation_requests;
    g_stop = 1;
    assert(!kick_negotiate_pusher_connection(&config, fake_connection_post));
    g_stop = 0;
    strcpy(g_kick_channel_id, "../42");
    assert(!kick_negotiate_pusher_connection(&config, fake_connection_post));
    assert(negotiation_requests == requests);
    strcpy(g_kick_channel_id, "42");
    g_kick_chat_token[0] = '\0';
    puts("PASS Kick negotiates fresh channel-specific Pusher settings without account credentials");
}


static size_t append_server_text(uint8_t *bytes, size_t offset, const char *text) {
    size_t length = strlen(text);
    assert(length < 65536 && offset + length + 4 < 4096);
    bytes[offset++] = 0x81;
    if (length < 126) bytes[offset++] = (uint8_t)length;
    else {
        bytes[offset++] = 126;
        bytes[offset++] = (uint8_t)(length >> 8);
        bytes[offset++] = (uint8_t)length;
    }
    memcpy(bytes + offset, text, length);
    return offset + length;
}

static bool sent_client_text(const char *needle) {
    size_t offset = 0;
    while (offset + 4 <= outgoing_size && memcmp(outgoing + offset, "\r\n\r\n", 4)) offset++;
    assert(offset + 4 <= outgoing_size);
    offset += 4;
    while (offset + 6 <= outgoing_size) {
        unsigned opcode = outgoing[offset++] & 15;
        size_t length = outgoing[offset++] & 127;
        if (length == 126) { length = ((size_t)outgoing[offset] << 8) | outgoing[offset + 1]; offset += 2; }
        assert(length < 1024 && offset + 4 + length <= outgoing_size);
        char decoded[1024];
        for (size_t i = 0; i < length; i++) decoded[i] = (char)(outgoing[offset + 4 + i] ^ outgoing[offset + (i & 3)]);
        decoded[length] = '\0';
        if (opcode == 1 && strstr(decoded, needle)) return true;
        offset += 4 + length;
    }
    return false;
}

static void test_kick_session_migration(void) {
    uint8_t frames[4096];
    size_t size = append_server_text(frames, 0,
        "{\"event\":\"pusher:error\",\"data\":{\"code\":4003,\"message\":\"Application disabled\"}}");
    set_input("", 0, 3);
    negotiation_status = 200;
    negotiation_json = "{\"data\":{\"mode\":\"websocket\",\"connections\":["
        "{\"provider\":\"pusher\",\"credentials\":{\"app_key\":\"retiredKey\",\"cluster\":\"us2\"}}]}}";
    kick_pusher_connection_t config;
    assert(kick_parse_pusher_connection(negotiation_json, strlen(negotiation_json), &config));
    expected_handshake = &config;
    automatic_upgrade = true;
    upgrade_frames = frames;
    upgrade_frames_size = size;
    queue_init(&g_queue);
    int before = negotiation_requests;
    kick_session(fake_connection_post);
    assert(!sent_client_text("pusher:subscribe"));
    bool rejected = false;
    for (int i = 0; i < g_queue.count; i++) if (strstr(g_queue.buf[i].text, "4003")) rejected = true;
    assert(rejected);
    DeleteCriticalSection(&g_queue.cs);

    size = append_server_text(frames, 0,
        "{\"event\":\"pusher:connection_established\",\"data\":\"{\\\"socket_id\\\":\\\"123.456\\\",\\\"activity_timeout\\\":120}\"}");
    size_t connection_size = size;
    size = append_server_text(frames, size,
        "{\"event\":\"pusher_internal:subscription_succeeded\",\"channel\":\"chatrooms.9001.v2\",\"data\":{}}");
    size = append_server_text(frames, size, "{\"event\":\"pusher:ping\",\"data\":{}}");
    size = append_server_text(frames, size,
        "{\"event\":\"App\\\\Events\\\\ChatMessageEvent\",\"data\":\"{\\\"content\\\":\\\"live fixture delivered\\\",\\\"sender\\\":{\\\"username\\\":\\\"viewer\\\"}}\"}");
    frames[size++] = 0x88; frames[size++] = 2; frames[size++] = 3; frames[size++] = 232; /* Normal close. */
    set_input("", 0, 3);
    negotiation_json = "{\"data\":{\"mode\":\"websocket\",\"connections\":["
        "{\"provider\":\"pusher\",\"credentials\":{\"app_key\":\"rotatedKey\",\"cluster\":\"eu\"}}]}}";
    assert(kick_parse_pusher_connection(negotiation_json, strlen(negotiation_json), &config));
    expected_handshake = &config;
    automatic_upgrade = true;
    upgrade_frames = frames;
    upgrade_frames_size = size;
    established_frame_size = connection_size;
    strcpy(g_channel, "blueface");
    queue_init(&g_queue);
    kick_session(fake_connection_post);
    assert(negotiation_requests == before + 2);
    assert(sent_client_text("chatrooms.9001.v2") && sent_client_text("pusher:pong"));
    int delivered = 0;
    for (int i = 0; i < g_queue.count; i++) {
        if (!g_queue.buf[i].is_system && strcmp(g_queue.buf[i].text, "live fixture delivered") == 0) delivered++;
    }
    assert(delivered == 1);
    DeleteCriticalSection(&g_queue.cs);
    puts("PASS native Kick session renegotiates after rejection, waits for acknowledgement and delivers live chat");
}

static void test_kick_rejection_diagnostics(void) {
    queue_init(&g_queue);
    kick_handle_pusher_text(connection,
        "{\"event\":\"pusher:error\",\"data\":{\"code\":4003,\"message\":\"Application disabled\"}}");
    assert(g_queue.count == 1);
    assert(strstr(g_queue.buf[0].text, "4003"));
    assert(strstr(g_queue.buf[0].text, "Application disabled"));
    DeleteCriticalSection(&g_queue.cs);
    puts("PASS Kick rejection reason reaches chat instead of a blind reconnect loop");
}

static void test_channel_arguments(const char *directory) {
    char state_path[MAX_PATH];
    snprintf(state_path, sizeof(state_path), "%s/channel-arguments", directory);
    char *arguments[] = {"overlay", "--channel", "MiXed.Name-123", "--provider", "kick",
        "--kick-chatroom-id", "9001", "--position-state-path", state_path};
    int count = (int)(sizeof(arguments) / sizeof(arguments[0]));
    assert(parse_args(count, arguments));
    assert(strcmp(g_channel, "mixed.name-123") == 0);

    char longest[81];
    memset(longest, 'a', sizeof(longest) - 1);
    longest[sizeof(longest) - 1] = '\0';
    arguments[2] = longest;
    assert(parse_args(count, arguments));
    assert(strcmp(g_channel, longest) == 0);
    queue_init(&g_queue);
    report_joined_channel();
    assert(g_queue.count == 1);
    assert(strlen(g_queue.buf[0].text) == strlen(longest) + strlen("joined #"));
    assert(strcmp(g_queue.buf[0].text + strlen("joined #"), longest) == 0);
    DeleteCriticalSection(&g_queue.cs);

    char oversized[82];
    memset(oversized, 'b', sizeof(oversized) - 1);
    oversized[sizeof(oversized) - 1] = '\0';
    arguments[2] = oversized;
    assert(!parse_args(count, arguments));
    arguments[2] = ".";
    assert(!parse_args(count, arguments));
    arguments[2] = "..";
    assert(!parse_args(count, arguments));

    arguments[2] = "MiXed.Name";
    arguments[4] = "twitch";
    assert(parse_args(count, arguments));
    assert(strcmp(g_channel, "mixed_name") == 0);
    puts("PASS channel arguments preserve valid Kick names and reject truncation");
}

int main(int argc, char **argv) {
    /* Windows PowerShell 5 treats redirected native stderr as a terminating error.
     * Keep both application diagnostics and assertion failures in the test log. */
    assert(_dup2(_fileno(stdout), _fileno(stderr)) == 0);
    test_kick_connection_negotiation();
    test_kick_rejection_diagnostics();
    test_kick_session_migration();
    test_upgrade();
    test_text_messages();
    test_invalid_frames();
    test_irc_overflow();
    test_channel_arguments(argc > 1 ? argv[1] : ".");
    return 0;
}
