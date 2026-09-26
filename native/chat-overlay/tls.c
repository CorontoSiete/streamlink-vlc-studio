/*
 * tls.c -- SChannel TLS 1.2/1.3 client.
 *
 * The flow is:
 *   1. WSAStartup (in tls_global_init).
 *   2. AcquireCredentialsHandleA(NULL, "Microsoft Unified Security Protocol Provider",
 *                                SECPKG_CRED_OUTBOUND, ...).
 *   3. socket() + connect() to host:port.
 *   4. InitializeSecurityContextA loop:
 *      - First call: pszTargetName = host, no input buffers.
 *      - Returns SEC_I_CONTINUE_NEEDED; send output token; recv more from peer.
 *      - Pass peer bytes back in input buffers; loop until SEC_E_OK.
 *   5. QueryContextAttributes(SECPKG_ATTR_STREAM_SIZES) for header/trailer/max msg.
 *   6. To send: build [header][data][trailer], EncryptMessage, write all.
 *      To recv: read into ring; DecryptMessage; SEC_E_INCOMPLETE_MESSAGE -> read more.
 *
 * The receive ring keeps undecrypted plus already-decrypted-but-unread bytes
 * so caller's tls_recv(buf, n) hands back small reads without losing data.
 */

#define WIN32_LEAN_AND_MEAN
#define SECURITY_WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <schannel.h>
#include <sspi.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "tls.h"

#ifdef _MSC_VER
#pragma comment(lib, "secur32.lib")
#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "crypt32.lib")
#endif

#define TLS_BUF_SIZE   16640    /* 16 KB + room for TLS overhead */
#define MAX_HOST_LEN   255

struct tls_conn {
    SOCKET                  sock;
    CredHandle              cred;
    CtxtHandle              ctx;
    bool                    have_cred;
    bool                    have_ctx;

    /* StreamSizes after handshake. */
    DWORD                   stream_header;
    DWORD                   stream_trailer;
    DWORD                   stream_max_msg;

    /* Undecrypted bytes buffered from socket. */
    BYTE                    in_buf[TLS_BUF_SIZE];
    DWORD                   in_len;

    /* Decrypted-but-not-yet-returned payload bytes. */
    BYTE                   *plain_data;   /* points into in_buf after decrypt */
    DWORD                   plain_len;

    /* Send scratch (header + payload + trailer fit here). */
    BYTE                    send_buf[TLS_BUF_SIZE];

    char                    host[MAX_HOST_LEN + 1];
};

static char g_last_error[256];

static void set_error(const char *fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(g_last_error, sizeof(g_last_error), fmt, ap);
    va_end(ap);
}

const char *tls_last_error(void) {
    return g_last_error;
}

/* ----- global init/cleanup ---------------------------------------------- */

bool tls_global_init(void) {
    WSADATA wsa;
    int rc = WSAStartup(MAKEWORD(2, 2), &wsa);
    if (rc != 0) {
        set_error("WSAStartup failed: %d", rc);
        return false;
    }
    return true;
}

void tls_global_cleanup(void) {
    WSACleanup();
}

/* ----- helpers ---------------------------------------------------------- */

static int recv_some(SOCKET s, void *buf, int n) {
    int got = recv(s, (char *)buf, n, 0);
    if (got == SOCKET_ERROR) {
        set_error("recv: WSA %d", WSAGetLastError());
        return -1;
    }
    return got;   /* may be 0 on graceful close */
}

static int send_all(SOCKET s, const void *buf, int n) {
    const char *p = (const char *)buf;
    int left = n;
    while (left > 0) {
        int sent = send(s, p, left, 0);
        if (sent == SOCKET_ERROR) {
            set_error("send: WSA %d", WSAGetLastError());
            return -1;
        }
        if (sent == 0) {
            set_error("send returned 0");
            return -1;
        }
        p += sent;
        left -= sent;
    }
    return n;
}

/* ----- SChannel handshake ---------------------------------------------- */

static bool send_handshake_token(tls_conn_t *t, SecBuffer *token) {
    bool ok = true;
    if (token->pvBuffer) {
        if (token->cbBuffer > 0) {
            ok = send_all(t->sock, token->pvBuffer, (int)token->cbBuffer) >= 0;
        }
        FreeContextBuffer(token->pvBuffer);
        token->pvBuffer = NULL;
        token->cbBuffer = 0;
    }
    return ok;
}

static bool retain_handshake_extra(tls_conn_t *t, const SecBuffer *extra) {
    DWORD remaining = extra->BufferType == SECBUFFER_EXTRA ? extra->cbBuffer : 0;
    if (remaining > t->in_len) {
        set_error("TLS handshake returned invalid extra bytes");
        return false;
    }
    if (remaining > 0) {
        memmove(t->in_buf, t->in_buf + t->in_len - remaining, remaining);
    }
    t->in_len = remaining;
    return true;
}

static bool client_handshake(tls_conn_t *t) {
    SECURITY_STATUS ss;
    SecBufferDesc   out_desc, in_desc;
    SecBuffer       out_bufs[1], in_bufs[2];
    DWORD           flags_in = ISC_REQ_SEQUENCE_DETECT | ISC_REQ_REPLAY_DETECT
                             | ISC_REQ_CONFIDENTIALITY | ISC_REQ_ALLOCATE_MEMORY
                             | ISC_REQ_STREAM | ISC_REQ_USE_SUPPLIED_CREDS;
    DWORD           flags_out = 0;

    /* First call: no input, request initial token. */
    out_bufs[0].BufferType = SECBUFFER_TOKEN;
    out_bufs[0].pvBuffer   = NULL;
    out_bufs[0].cbBuffer   = 0;
    out_desc.ulVersion = SECBUFFER_VERSION;
    out_desc.cBuffers  = 1;
    out_desc.pBuffers  = out_bufs;

    SecInvalidateHandle(&t->ctx);
    ss = InitializeSecurityContextA(&t->cred, NULL, t->host,
                                    flags_in, 0, 0, NULL, 0,
                                    &t->ctx, &out_desc, &flags_out, NULL);
    t->have_ctx = SecIsValidHandle(&t->ctx);
    if (!send_handshake_token(t, &out_bufs[0])) return false;
    if (ss != SEC_I_CONTINUE_NEEDED) {
        set_error("InitializeSecurityContext (first) failed: 0x%08lx", ss);
        return false;
    }
    /* Process buffered handshake records before waiting for additional TCP data. */
    t->in_len = 0;
    bool need_more = true;
    while (true) {
        if (need_more) {
            if (t->in_len >= sizeof(t->in_buf)) {
                set_error("TLS handshake record exceeds receive buffer");
                return false;
            }
            int got = recv_some(t->sock, t->in_buf + t->in_len,
                                (int)(sizeof(t->in_buf) - t->in_len));
            if (got <= 0) {
                set_error("handshake: peer closed during read (got=%d)", got);
                return false;
            }
            t->in_len += (DWORD)got;
        }

        in_bufs[0].BufferType = SECBUFFER_TOKEN;
        in_bufs[0].pvBuffer   = t->in_buf;
        in_bufs[0].cbBuffer   = t->in_len;
        in_bufs[1].BufferType = SECBUFFER_EMPTY;
        in_bufs[1].pvBuffer   = NULL;
        in_bufs[1].cbBuffer   = 0;
        in_desc.ulVersion = SECBUFFER_VERSION;
        in_desc.cBuffers  = 2;
        in_desc.pBuffers  = in_bufs;

        out_bufs[0].BufferType = SECBUFFER_TOKEN;
        out_bufs[0].pvBuffer   = NULL;
        out_bufs[0].cbBuffer   = 0;
        out_desc.cBuffers = 1;

        ss = InitializeSecurityContextA(&t->cred, &t->ctx, t->host,
                                        flags_in, 0, 0,
                                        &in_desc, 0,
                                        NULL, &out_desc, &flags_out, NULL);

        if (ss == SEC_E_INCOMPLETE_MESSAGE) {
            /* Need more bytes; keep what we have, loop and recv more. */
            if (out_bufs[0].pvBuffer) FreeContextBuffer(out_bufs[0].pvBuffer);
            need_more = true;
            continue;
        }
        if (!send_handshake_token(t, &out_bufs[0])) return false;
        if (ss == SEC_E_OK || ss == SEC_I_CONTINUE_NEEDED) {
            if (!retain_handshake_extra(t, &in_bufs[1])) return false;
            if (ss == SEC_E_OK) break;
            need_more = t->in_len == 0;
            continue;
        }
        set_error("InitializeSecurityContext failed: 0x%08lx", ss);
        return false;
    }

    SecPkgContext_StreamSizes sizes;
    ss = QueryContextAttributesA(&t->ctx, SECPKG_ATTR_STREAM_SIZES, &sizes);
    if (ss != SEC_E_OK) {
        set_error("QueryContextAttributes(STREAM_SIZES) failed: 0x%08lx", ss);
        return false;
    }
    t->stream_header  = sizes.cbHeader;
    t->stream_trailer = sizes.cbTrailer;
    t->stream_max_msg = sizes.cbMaximumMessage;
    return true;
}

/* ----- TCP connect ------------------------------------------------------ */

static SOCKET tcp_connect(const char *host, const char *port) {
    struct addrinfo hints, *res = NULL;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family   = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    int rc = getaddrinfo(host, port, &hints, &res);
    if (rc != 0) {
        set_error("getaddrinfo(%s:%s): %d", host, port, rc);
        return INVALID_SOCKET;
    }
    SOCKET s = INVALID_SOCKET;
    for (struct addrinfo *ai = res; ai != NULL; ai = ai->ai_next) {
        s = socket(ai->ai_family, ai->ai_socktype, ai->ai_protocol);
        if (s == INVALID_SOCKET) continue;
        if (connect(s, ai->ai_addr, (int)ai->ai_addrlen) == 0) {
            break;
        }
        closesocket(s);
        s = INVALID_SOCKET;
    }
    freeaddrinfo(res);
    if (s == INVALID_SOCKET) {
        set_error("connect(%s:%s) failed", host, port);
    }
    return s;
}

/* ----- public API ------------------------------------------------------- */

tls_conn_t *tls_connect(const char *host, const char *port) {
    tls_conn_t *t = (tls_conn_t *)calloc(1, sizeof(*t));
    if (!t) { set_error("oom"); return NULL; }
    t->sock = INVALID_SOCKET;
    strncpy(t->host, host, MAX_HOST_LEN);
    t->host[MAX_HOST_LEN] = '\0';

    SCHANNEL_CRED cred = {0};
    cred.dwVersion             = SCHANNEL_CRED_VERSION;
    cred.dwFlags               = SCH_CRED_AUTO_CRED_VALIDATION
                               | SCH_CRED_NO_DEFAULT_CREDS
                               | SCH_USE_STRONG_CRYPTO;
    SECURITY_STATUS ss = AcquireCredentialsHandleA(
        NULL, (SEC_CHAR *)UNISP_NAME_A, SECPKG_CRED_OUTBOUND,
        NULL, &cred, NULL, NULL, &t->cred, NULL);
    if (ss != SEC_E_OK) {
        set_error("AcquireCredentialsHandle failed: 0x%08lx", ss);
        tls_close(t);
        return NULL;
    }
    t->have_cred = true;

    t->sock = tcp_connect(host, port);
    if (t->sock == INVALID_SOCKET) {
        tls_close(t);
        return NULL;
    }

    if (!client_handshake(t)) {
        tls_close(t);
        return NULL;
    }
    return t;
}

int tls_send(tls_conn_t *t, const void *data, int n) {
    if (!t || n <= 0) return n;
    const BYTE *src = (const BYTE *)data;
    int left = n;
    while (left > 0) {
        DWORD max_chunk;
        if (t->stream_header + t->stream_trailer >= sizeof(t->send_buf)) {
            set_error("TLS stream overhead exceeds send buffer");
            return -1;
        }

        max_chunk = (DWORD)(sizeof(t->send_buf) - t->stream_header - t->stream_trailer);
        if (max_chunk > t->stream_max_msg) max_chunk = t->stream_max_msg;
        if (max_chunk == 0) {
            set_error("TLS stream maximum message size is zero");
            return -1;
        }

        DWORD chunk = (DWORD)left;
        if (chunk > max_chunk) chunk = max_chunk;

        BYTE *hdr_p  = t->send_buf;
        BYTE *body_p = hdr_p + t->stream_header;
        BYTE *tail_p = body_p + chunk;
        memcpy(body_p, src, chunk);

        SecBuffer bufs[4];
        bufs[0].BufferType = SECBUFFER_STREAM_HEADER;
        bufs[0].pvBuffer   = hdr_p;
        bufs[0].cbBuffer   = t->stream_header;
        bufs[1].BufferType = SECBUFFER_DATA;
        bufs[1].pvBuffer   = body_p;
        bufs[1].cbBuffer   = chunk;
        bufs[2].BufferType = SECBUFFER_STREAM_TRAILER;
        bufs[2].pvBuffer   = tail_p;
        bufs[2].cbBuffer   = t->stream_trailer;
        bufs[3].BufferType = SECBUFFER_EMPTY;
        bufs[3].pvBuffer   = NULL;
        bufs[3].cbBuffer   = 0;
        SecBufferDesc desc;
        desc.ulVersion = SECBUFFER_VERSION;
        desc.cBuffers  = 4;
        desc.pBuffers  = bufs;
        SECURITY_STATUS ss = EncryptMessage(&t->ctx, 0, &desc, 0);
        if (ss != SEC_E_OK) {
            set_error("EncryptMessage: 0x%08lx", ss);
            return -1;
        }
        DWORD total = bufs[0].cbBuffer + bufs[1].cbBuffer + bufs[2].cbBuffer;
        if (send_all(t->sock, hdr_p, (int)total) < 0) {
            return -1;
        }
        src  += chunk;
        left -= (int)chunk;
    }
    return n;
}

int tls_recv(tls_conn_t *t, void *out, int n) {
    if (!t || n <= 0) return 0;
    BYTE *dst = (BYTE *)out;

    /* Hand back already-decrypted leftovers first. */
    if (t->plain_len > 0) {
        DWORD take = t->plain_len < (DWORD)n ? t->plain_len : (DWORD)n;
        memcpy(dst, t->plain_data, take);
        t->plain_data += take;
        t->plain_len  -= take;
        return (int)take;
    }

    while (true) {
        /* If we have unread encrypted bytes, try to decrypt them first. */
        if (t->in_len > 0) {
            SecBuffer bufs[4];
            bufs[0].BufferType = SECBUFFER_DATA;
            bufs[0].pvBuffer   = t->in_buf;
            bufs[0].cbBuffer   = t->in_len;
            bufs[1].BufferType = SECBUFFER_EMPTY;
            bufs[2].BufferType = SECBUFFER_EMPTY;
            bufs[3].BufferType = SECBUFFER_EMPTY;
            SecBufferDesc desc;
            desc.ulVersion = SECBUFFER_VERSION;
            desc.cBuffers  = 4;
            desc.pBuffers  = bufs;
            SECURITY_STATUS ss = DecryptMessage(&t->ctx, &desc, 0, NULL);

            if (ss == SEC_E_OK) {
                /* Find DATA + EXTRA buffers. */
                BYTE  *plain_p = NULL;
                DWORD  plain_n = 0;
                BYTE  *extra_p = NULL;
                DWORD  extra_n = 0;
                BYTE   plain_copy[TLS_BUF_SIZE];
                for (int i = 1; i < 4; i++) {
                    if (bufs[i].BufferType == SECBUFFER_DATA  && !plain_p) {
                        plain_p = (BYTE *)bufs[i].pvBuffer;
                        plain_n = bufs[i].cbBuffer;
                    } else if (bufs[i].BufferType == SECBUFFER_EXTRA && !extra_p) {
                        extra_p = (BYTE *)bufs[i].pvBuffer;
                        extra_n = bufs[i].cbBuffer;
                    }
                }

                if (plain_n > sizeof(plain_copy)) {
                    set_error("decrypted TLS payload exceeds receive buffer");
                    return -1;
                }
                if (plain_n > 0 && !plain_p) {
                    set_error("TLS decrypt returned payload size without data");
                    return -1;
                }
                if (plain_n > 0) {
                    memcpy(plain_copy, plain_p, plain_n);
                }

                /* Move any leftover encrypted bytes to the front of in_buf. */
                if (extra_p && extra_n > 0) {
                    memmove(t->in_buf, extra_p, extra_n);
                    t->in_len = extra_n;
                } else {
                    t->in_len = 0;
                }

                if (plain_n == 0) {
                    /* Empty TLS record (heartbeat); loop and keep reading. */
                    continue;
                }

                DWORD take = plain_n < (DWORD)n ? plain_n : (DWORD)n;
                memcpy(dst, plain_copy, take);
                if (take < plain_n) {
                    DWORD keep = plain_n - take;
                    BYTE *stash = t->in_buf + t->in_len;
                    if ((DWORD)(sizeof(t->in_buf) - t->in_len) < keep) {
                        set_error("not enough room to stash decrypted TLS bytes");
                        return -1;
                    }
                    memcpy(stash, plain_copy + take, keep);
                    t->plain_data = stash;
                    t->plain_len  = keep;
                }
                return (int)take;
            }
            if (ss == SEC_E_INCOMPLETE_MESSAGE) {
                /* fallthrough to recv more from socket */
            } else if (ss == SEC_I_CONTEXT_EXPIRED) {
                return 0;   /* clean TLS close */
            } else if (ss == SEC_I_RENEGOTIATE) {
                set_error("renegotiation requested, unsupported");
                return -1;
            } else {
                set_error("DecryptMessage: 0x%08lx", ss);
                return -1;
            }
        }

        /* Read more encrypted bytes. */
        if (t->in_len >= sizeof(t->in_buf)) {
            set_error("recv buffer full but message still incomplete");
            return -1;
        }
        int got = recv_some(t->sock, t->in_buf + t->in_len,
                             (int)(sizeof(t->in_buf) - t->in_len));
        if (got < 0)   return -1;
        if (got == 0)  return 0;
        t->in_len += (DWORD)got;
    }
}

void tls_close(tls_conn_t *t) {
    if (!t) return;
    if (t->have_ctx)  DeleteSecurityContext(&t->ctx);
    if (t->have_cred) FreeCredentialsHandle(&t->cred);
    if (t->sock != INVALID_SOCKET) closesocket(t->sock);
    free(t);
}
