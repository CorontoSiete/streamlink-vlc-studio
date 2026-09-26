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
static char upgrade_response[1024];
static tls_conn_t *const connection = (tls_conn_t *)(uintptr_t)1;

static void set_input(const void *bytes, size_t length, size_t chunk) {
    incoming = bytes;
    incoming_size = length;
    incoming_offset = outgoing_size = 0;
    read_chunk = chunk;
    fail_send = automatic_upgrade = false;
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
        const char *value = strstr(request, "Sec-WebSocket-Key: ");
        assert(value);
        memcpy(key, value + strlen("Sec-WebSocket-Key: "), 24);
        key[24] = '\0';
        assert(make_websocket_accept(key, accept, sizeof(accept)));
        int count = snprintf(upgrade_response, sizeof(upgrade_response),
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            "Sec-WebSocket-Accept: %s\r\n\r\n", accept);
        assert(count > 0 && (size_t)count + 3 < sizeof(upgrade_response));
        upgrade_response[count] = (char)0x81;
        upgrade_response[count + 1] = 1;
        upgrade_response[count + 2] = '!';
        incoming = (const uint8_t *)upgrade_response;
        incoming_size = (size_t)count + 3;
        incoming_offset = 0;
        automatic_upgrade = false;
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
    assert(websocket_handshake(connection));
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
        assert(!websocket_handshake(connection));
    }
    /* A 101 at the front of an unterminated oversized header used to pass. */
    memset(response, 'x', sizeof(response));
    memcpy(response, "HTTP/1.1 101 Switching Protocols\r\nX: ", 36);
    set_input(response, sizeof(response), 1);
    assert(!websocket_handshake(connection));
    set_input("", 0, 1);
    g_stop = 1;
    assert(!websocket_handshake(connection));
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

int main(void) {
    /* Windows PowerShell 5 treats redirected native stderr as a terminating error.
     * Keep both application diagnostics and assertion failures in the test log. */
    assert(_dup2(_fileno(stdout), _fileno(stderr)) == 0);
    test_upgrade();
    test_text_messages();
    test_invalid_frames();
    test_irc_overflow();
    return 0;
}
