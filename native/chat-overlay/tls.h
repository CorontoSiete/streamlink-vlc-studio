/*
 * tls.h -- minimal SChannel TLS 1.2 client wrapper.
 *
 * One connection at a time per `tls_conn_t`. No client certs, no ALPN.
 * Hostname validation is on (SCH_CRED_AUTO_CRED_VALIDATION + server name
 * passed into InitializeSecurityContext SNI).
 *
 * Lifecycle:
 *   tls_global_init()
 *   tls_conn_t *t = tls_connect(host, port);
 *   tls_send(t, ...) / tls_recv(t, ...)  until done
 *   tls_close(t)
 *   tls_global_cleanup()
 *
 * tls_recv returns >0 on bytes, 0 on clean close, -1 on error.
 * tls_send returns >=0 bytes written, -1 on error. Writes are all-or-error.
 */

#ifndef VLC_CHAT_OVERLAY_TLS_H
#define VLC_CHAT_OVERLAY_TLS_H

#include <stdbool.h>
#include <stddef.h>

typedef struct tls_conn tls_conn_t;

bool         tls_global_init  (void);
void         tls_global_cleanup(void);

tls_conn_t  *tls_connect      (const char *host, const char *port);
void         tls_close        (tls_conn_t *conn);

/* Returns bytes written (== n on success), -1 on error. */
int          tls_send         (tls_conn_t *conn, const void *data, int n);

/* Returns bytes read (1..n), 0 on clean close, -1 on error. */
int          tls_recv         (tls_conn_t *conn, void *buf, int n);

/* Last error message (static buffer; overwritten by subsequent calls). */
const char  *tls_last_error   (void);

#endif
