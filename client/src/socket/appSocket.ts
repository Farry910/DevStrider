import { io, type Socket } from 'socket.io-client';

const TOKEN_KEY = 'devstrider_token';

function readToken(): string {
  return localStorage.getItem(TOKEN_KEY) ?? '';
}

let socket: Socket | null = null;

/**
 * Single Socket.IO connection for the app (presence, bid-board hints, etc.).
 * Uses aggressive reconnection and refreshes JWT from localStorage before each reconnect attempt.
 */
export function getAppSocket(): Socket {
  if (!socket) {
    socket = io({
      path: '/socket.io',
      auth: { token: readToken() },
      transports: ['websocket', 'polling'],
      withCredentials: true,
      reconnection: true,
      reconnectionAttempts: Infinity,
      reconnectionDelay: 1000,
      reconnectionDelayMax: 10_000,
      randomizationFactor: 0.5,
      timeout: 20_000,
      autoConnect: true,
    });

    // Fresh token on every reconnect handshake (token refresh / login without full reload).
    socket.io.on('reconnect_attempt', () => {
      socket!.auth = { token: readToken() };
    });

    socket.io.on('reconnect', () => {
      socket!.auth = { token: readToken() };
    });

    socket.on('connect', () => {
      socket!.auth = { token: readToken() };
    });
  }
  return socket;
}

/** After login / register / token change: update auth and connect if needed. */
export function syncAppSocketAuth(): void {
  const t = readToken();
  const s = getAppSocket();
  s.auth = { token: t };
  if (t) {
    if (!s.connected) s.connect();
  } else {
    s.disconnect();
  }
}

/** On logout: disconnect (keeps singleton; listeners remain on the socket instance). */
export function disconnectAppSocket(): void {
  if (socket?.connected) socket.disconnect();
}
