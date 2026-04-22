import { verifyToken } from '../utils/jwt.js';
import { User } from '../models/User.js';
import { assertGroupMember } from '../services/membership.js';
import { isAllowedAvatarId } from '../constants/avatarPresets.js';

function isMongoId(s) {
  return typeof s === 'string' && /^[a-f\d]{24}$/i.test(s);
}

function presenceRoom(groupId) {
  return `presence:${groupId}`;
}

function bidBoardRoom(groupId) {
  return `bidboard:${groupId}`;
}

/** @type {import('socket.io').Server | null} */
let ioInstance = null;

/** Notify members subscribed to this group’s bid board to refetch (REST remains source of truth). */
export function emitBidBoardInvalidate(groupId) {
  if (!ioInstance || !groupId) return;
  const g = String(groupId);
  ioInstance.to(bidBoardRoom(g)).emit('bidboard:invalidate', { groupId: g, t: Date.now() });
}

/** @type {Map<string, Map<string, number>>} groupId -> userId -> refCount */
const presenceByGroup = new Map();
/** @type {Map<string, Set<string>>} socket.id -> groupIds */
const socketPresenceGroups = new Map();

function presenceAdd(groupId, userId) {
  const g = String(groupId);
  const u = String(userId);
  if (!presenceByGroup.has(g)) presenceByGroup.set(g, new Map());
  const m = presenceByGroup.get(g);
  m.set(u, (m.get(u) || 0) + 1);
}

function presenceRemove(groupId, userId) {
  const g = String(groupId);
  const u = String(userId);
  const m = presenceByGroup.get(g);
  if (!m) return;
  const n = (m.get(u) || 1) - 1;
  if (n <= 0) m.delete(u);
  else m.set(u, n);
  if (m.size === 0) presenceByGroup.delete(g);
}

async function emitPresenceSnapshot(io, groupId) {
  const g = String(groupId);
  const m = presenceByGroup.get(g);
  const ids = m ? [...m.keys()] : [];
  if (ids.length === 0) {
    io.to(presenceRoom(g)).emit('presence:update', { users: [] });
    return;
  }
  const users = await User.find({ _id: { $in: ids } })
    .select('nickname avatarId')
    .lean();
  const byId = new Map(users.map((u) => [String(u._id), u]));
  const list = ids.map((id) => {
    const u = byId.get(id);
    const av = u?.avatarId && isAllowedAvatarId(u.avatarId) ? u.avatarId : 'initial';
    return {
      id,
      nickname: u?.nickname ?? '',
      avatarId: av,
    };
  });
  io.to(presenceRoom(g)).emit('presence:update', { users: list });
}

/**
 * @param {import('socket.io').Server} io
 */
export function registerHexGameSocket(io) {
  ioInstance = io;

  io.use((socket, next) => {
    try {
      const token = socket.handshake.auth?.token;
      if (!token || typeof token !== 'string') {
        return next(new Error('Unauthorized'));
      }
      const decoded = verifyToken(token);
      socket.userId = decoded.sub;
      return next();
    } catch {
      return next(new Error('Unauthorized'));
    }
  });

  io.on('connection', (socket) => {
    const userId = socket.userId;

    socket.on('presence:join', async (groupId, cb) => {
      try {
        if (!isMongoId(groupId)) {
          cb?.({ error: 'Invalid groupId' });
          return;
        }
        const m = await assertGroupMember(userId, groupId);
        if (!m.ok) {
          cb?.({ error: m.error });
          return;
        }
        const g = String(groupId);
        await socket.join(presenceRoom(g));
        presenceAdd(g, userId);
        if (!socketPresenceGroups.has(socket.id)) socketPresenceGroups.set(socket.id, new Set());
        socketPresenceGroups.get(socket.id).add(g);
        await emitPresenceSnapshot(io, g);
        cb?.({ ok: true });
      } catch (e) {
        cb?.({ error: e?.message || 'presence join failed' });
      }
    });

    socket.on('presence:leave', async (groupId) => {
      if (!isMongoId(groupId)) return;
      const g = String(groupId);
      await socket.leave(presenceRoom(g));
      presenceRemove(g, userId);
      const sg = socketPresenceGroups.get(socket.id);
      if (sg) sg.delete(g);
      await emitPresenceSnapshot(io, g);
    });

    socket.on('bidboard:join', async (groupId, cb) => {
      try {
        if (!isMongoId(groupId)) {
          cb?.({ error: 'Invalid groupId' });
          return;
        }
        const m = await assertGroupMember(userId, groupId);
        if (!m.ok) {
          cb?.({ error: m.error });
          return;
        }
        await socket.join(bidBoardRoom(String(groupId)));
        cb?.({ ok: true });
      } catch (e) {
        cb?.({ error: e?.message || 'bidboard join failed' });
      }
    });

    socket.on('bidboard:leave', async (groupId) => {
      if (!isMongoId(groupId)) return;
      await socket.leave(bidBoardRoom(String(groupId)));
    });

    socket.on('disconnect', async () => {
      const sg = socketPresenceGroups.get(socket.id);
      if (!sg) return;
      socketPresenceGroups.delete(socket.id);
      for (const g of sg) {
        presenceRemove(g, userId);
        await emitPresenceSnapshot(io, g);
      }
    });
  });
}
