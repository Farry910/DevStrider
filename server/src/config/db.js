import mongoose from 'mongoose';
import { UserBid } from '../models/UserBid.js';

/** Pool and timeout defaults tuned for API workloads; override via MONGODB_URI query params if needed. */
const CONNECT_OPTIONS = {
  maxPoolSize: Number(process.env.MONGODB_MAX_POOL_SIZE) || 20,
  minPoolSize: Number(process.env.MONGODB_MIN_POOL_SIZE) || 2,
  serverSelectionTimeoutMS: Number(process.env.MONGODB_SERVER_SELECTION_MS) || 10_000,
  socketTimeoutMS: Number(process.env.MONGODB_SOCKET_TIMEOUT_MS) || 45_000,
};

export async function connectDb(uri) {
  mongoose.set('strictQuery', true);
  await mongoose.connect(uri, CONNECT_OPTIONS);
  try {
    await UserBid.collection.updateMany(
      { $or: [{ firstCreatedAt: { $exists: false } }, { firstCreatedAt: null }] },
      [{ $set: { firstCreatedAt: '$createdAt' } }]
    );
  } catch (e) {
    console.warn('UserBid firstCreatedAt backfill skipped:', e?.message || e);
  }
}

export async function disconnectDb() {
  await mongoose.connection.close();
}
