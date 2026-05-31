import 'dotenv/config';
import { createServer } from 'http';
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import express from 'express';
import cors from 'cors';
import morgan from 'morgan';
import { Server } from 'socket.io';
import mongoose from 'mongoose';
import { connectDb, disconnectDb } from './config/db.js';
import { errorHandler } from './middleware/httpError.js';
import authRoutes from './routes/auth.routes.js';
import groupRoutes from './routes/group.routes.js';
import bidRoutes from './routes/bid.routes.js';
import interviewRoutes from './routes/interview.routes.js';
import statsRoutes from './routes/stats.routes.js';
import badgeRoutes from './routes/badge.routes.js';
import integrationBidAssistantRoutes from './routes/integrationBidAssistant.routes.js';
import profileRoutes from './routes/profile.routes.js';
import notificationRoutes from './routes/notification.routes.js';
import achievementRoutes from './routes/achievement.routes.js';
import platformAdminRoutes from './routes/platformAdmin.routes.js';
import exportRoutes from './routes/export.routes.js';
import { registerHexGameSocket } from './socket/hexGameSocket.js';
import { ensurePlatformAdmin } from './services/platformAdminSeed.js';
import { migrateLegacyGroups } from './services/legacyGroupMigration.js';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const CLIENT_DIST = path.resolve(__dirname, '../../client/dist');

const app = express();
const httpServer = createServer(app);
const PORT = Number(process.env.PORT) || 4000;
/** Bind address; 0.0.0.0 accepts connections on all interfaces (LAN / static IP). */
const HOST = process.env.HOST || '0.0.0.0';

const rawOrigins =
  process.env.CLIENT_ORIGIN || 'http://localhost:5173';
const CLIENT_ORIGINS = rawOrigins
  .split(',')
  .map((s) => s.trim())
  .filter(Boolean);
const corsOrigin =
  CLIENT_ORIGINS.length === 1 ? CLIENT_ORIGINS[0] : CLIENT_ORIGINS;

const CORS_EXTRA_ORIGINS = (process.env.CORS_EXTRA_ORIGINS || '')
  .split(',')
  .map((s) => s.trim())
  .filter(Boolean);

function isHttpCorsOriginAllowed(origin) {
  if (!origin) return true;
  if (CLIENT_ORIGINS.includes(origin)) return true;
  if (CORS_EXTRA_ORIGINS.includes(origin)) return true;
  return false;
}

const socketCorsOrigins = [...CLIENT_ORIGINS, ...CORS_EXTRA_ORIGINS];

const io = new Server(httpServer, {
  cors: {
    origin: socketCorsOrigins.length === 1 ? socketCorsOrigins[0] : socketCorsOrigins,
    credentials: true,
  },
});
registerHexGameSocket(io);

app.use(
  cors({
    origin: (origin, callback) => callback(null, isHttpCorsOriginAllowed(origin)),
    credentials: true,
  })
);
app.use(express.json({ limit: '5mb' }));
/**
 * Use `combined` (Apache-style) logs in production so they line up with nginx access logs;
 * keep the colourful `dev` format for local development.
 */
app.use(morgan(process.env.NODE_ENV === 'production' ? 'combined' : 'dev'));

app.get('/api/health', (_req, res) => {
  const dbUp = mongoose.connection.readyState === 1;
  res.status(dbUp ? 200 : 503).json({
    ok: dbUp,
    db: dbUp ? 'connected' : 'disconnected',
    t: new Date().toISOString(),
  });
});

app.use('/api/auth', authRoutes);
app.use('/api/groups', groupRoutes);
app.use('/api', badgeRoutes);
app.use('/api', bidRoutes);
app.use('/api', interviewRoutes);
app.use('/api', statsRoutes);
app.use('/api/integrations/bid-assistant', integrationBidAssistantRoutes);
app.use('/api/profile', profileRoutes);
app.use('/api/notifications', notificationRoutes);
app.use('/api', achievementRoutes);
app.use('/api/admin', platformAdminRoutes);
app.use('/api', exportRoutes);

app.use('/api', (_req, res) => {
  res.status(404).json({ error: 'Not found' });
});

if (fs.existsSync(CLIENT_DIST)) {
  app.use(express.static(CLIENT_DIST));
  app.get('*', (req, res, next) => {
    if (req.method !== 'GET') return next();
    res.sendFile(path.join(CLIENT_DIST, 'index.html'), (err) => {
      if (err) next(err);
    });
  });
}

app.use(errorHandler);

const uri = process.env.MONGODB_URI || 'mongodb://127.0.0.1:27017/devstrider';

function shutdown(signal) {
  console.log(`${signal}: shutting down…`);
  httpServer.close((err) => {
    if (err) console.error('HTTP server close error', err);
    io.close(() => {
      disconnectDb()
        .then(() => {
          console.log('MongoDB connection closed');
          process.exit(0);
        })
        .catch((e) => {
          console.error(e);
          process.exit(1);
        });
    });
  });
  setTimeout(() => {
    console.error('Forced exit after shutdown timeout');
    process.exit(1);
  }, 10_000).unref();
}

process.once('SIGTERM', () => shutdown('SIGTERM'));
process.once('SIGINT', () => shutdown('SIGINT'));

process.on('unhandledRejection', (reason) => {
  console.error('unhandledRejection', reason);
});
process.on('uncaughtException', (err) => {
  console.error('uncaughtException', err);
});

connectDb(uri)
  .then(async () => {
    await ensurePlatformAdmin().catch((e) => {
      console.error('[platformAdminSeed] failed', e);
    });
    await migrateLegacyGroups().catch((e) => {
      console.error('[legacyGroupMigration] failed', e);
    });
    httpServer.listen(PORT, HOST, () => {
      if (HOST === '0.0.0.0' || HOST === '::') {
        console.log(
          `API + Socket.IO listening on port ${PORT} (all interfaces — open http://localhost:${PORT} or http://<your-ip>:${PORT})`
        );
      } else {
        console.log(`API + Socket.IO listening on http://${HOST}:${PORT}`);
      }
    });
  })
  .catch((e) => {
    console.error('MongoDB connection failed', e);
    process.exit(1);
  });
