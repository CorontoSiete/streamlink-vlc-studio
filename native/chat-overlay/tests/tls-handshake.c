/* Real handshake loop, deterministic SChannel/TCP results. No sockets or credentials. */
#define WIN32_LEAN_AND_MEAN
#define SECURITY_WIN32
#include <winsock2.h>
#include <windows.h>
#include <schannel.h>
#include <sspi.h>
#include <assert.h>
#include <stdbool.h>
#include <string.h>

static SECURITY_STATUS SEC_ENTRY test_initialize(PCredHandle, PCtxtHandle, SEC_CHAR *,
    unsigned long, unsigned long, unsigned long, PSecBufferDesc, unsigned long,
    PCtxtHandle, PSecBufferDesc, unsigned long *, PTimeStamp);
static SECURITY_STATUS SEC_ENTRY test_query(PCtxtHandle, unsigned long, void *);
static SECURITY_STATUS SEC_ENTRY test_free(void *);
static int WSAAPI test_recv(SOCKET, char *, int, int);
static int WSAAPI test_send(SOCKET, const char *, int, int);
#define InitializeSecurityContextA test_initialize
#define QueryContextAttributesA test_query
#define FreeContextBuffer test_free
#define recv test_recv
#define send test_send
#include "../tls.c"

static const char wire[] = "ABCDxy";
static size_t wire_offset, chunk_size;
static int stage, reads, incomplete, allocated, freed;
static bool fail_first, fail_send;

static void token(PSecBufferDesc output, DWORD size) {
    output->pBuffers[0].pvBuffer = malloc(1);
    output->pBuffers[0].cbBuffer = size;
    assert(output->pBuffers[0].pvBuffer);
    *(char *)output->pBuffers[0].pvBuffer = '!';
    allocated++;
}

static SECURITY_STATUS SEC_ENTRY test_initialize(PCredHandle credential, PCtxtHandle context,
    SEC_CHAR *target, unsigned long flags, unsigned long reserved, unsigned long representation,
    PSecBufferDesc input, unsigned long reserved2, PCtxtHandle new_context,
    PSecBufferDesc output, unsigned long *out_flags, PTimeStamp expiry) {
    (void)credential; (void)context; (void)target; (void)flags; (void)reserved;
    (void)representation; (void)reserved2; (void)new_context; (void)out_flags; (void)expiry;
    if (!input) {
        token(output, 1);
        if (!fail_first) new_context->dwLower = new_context->dwUpper = 1;
        return fail_first ? SEC_E_INVALID_TOKEN : SEC_I_CONTINUE_NEEDED;
    }
    assert(stage < 2);
    SecBuffer *bytes = &input->pBuffers[0];
    if (bytes->cbBuffer < 2) { incomplete++; return SEC_E_INCOMPLETE_MESSAGE; }
    assert(memcmp(bytes->pvBuffer, wire + stage * 2, 2) == 0);
    input->pBuffers[1].BufferType = SECBUFFER_EXTRA;
    input->pBuffers[1].cbBuffer = bytes->cbBuffer - 2;
    /* The extra pointer is not a copy; the production code must use the count. */
    input->pBuffers[1].pvBuffer = NULL;
    token(output, 0); /* Even empty allocated output tokens must be released. */
    return ++stage == 2 ? SEC_E_OK : SEC_I_CONTINUE_NEEDED;
}

static SECURITY_STATUS SEC_ENTRY test_query(PCtxtHandle context, unsigned long attribute, void *output) {
    (void)context;
    assert(attribute == SECPKG_ATTR_STREAM_SIZES);
    SecPkgContext_StreamSizes *sizes = output;
    memset(sizes, 0, sizeof(*sizes));
    sizes->cbHeader = 5; sizes->cbTrailer = 16; sizes->cbMaximumMessage = 16384;
    return SEC_E_OK;
}

static SECURITY_STATUS SEC_ENTRY test_free(void *memory) {
    free(memory); freed++; return SEC_E_OK;
}

static int WSAAPI test_send(SOCKET socket, const char *bytes, int length, int flags) {
    (void)socket; (void)bytes; (void)flags;
    return fail_send ? SOCKET_ERROR : length;
}

static int WSAAPI test_recv(SOCKET socket, char *bytes, int length, int flags) {
    (void)socket; (void)flags;
    reads++;
    size_t size = sizeof(wire) - 1 - wire_offset;
    if (size > chunk_size) size = chunk_size;
    if (size > (size_t)length) size = (size_t)length;
    memcpy(bytes, wire + wire_offset, size);
    wire_offset += size;
    return (int)size;
}

static void reset(size_t chunk) {
    wire_offset = 0; chunk_size = chunk;
    stage = reads = incomplete = allocated = freed = 0;
    fail_first = fail_send = false;
}

int main(void) {
    for (size_t chunk = 1; chunk <= sizeof(wire) - 1; chunk++) {
        reset(chunk);
        tls_conn_t state = {0};
        assert(client_handshake(&state));
        assert(stage == 2 && allocated == freed && state.stream_max_msg == 16384 && state.have_ctx);
        assert(state.in_len == wire_offset - 4);
        assert(memcmp(state.in_buf, "xy", state.in_len) == 0);
        if (chunk == 1 || chunk == 3) assert(incomplete > 0);
        if (chunk == sizeof(wire) - 1) assert(reads == 1);
    }
    for (int failure = 0; failure < 2; failure++) {
        reset(6);
        fail_first = failure == 0;
        fail_send = failure == 1;
        tls_conn_t state = {0};
        assert(!client_handshake(&state));
        assert(allocated == 1 && freed == 1);
        assert(state.have_ctx == !fail_first);
    }
    puts("PASS TLS handshake consumes buffered records, reads incomplete records and frees every output token");
    return 0;
}
