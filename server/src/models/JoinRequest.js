import mongoose from 'mongoose';

const joinRequestSchema = new mongoose.Schema(
  {
    groupId: { type: mongoose.Schema.Types.ObjectId, ref: 'Group', required: true },
    userId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', required: true },
    status: {
      type: String,
      enum: ['pending', 'approved', 'rejected'],
      default: 'pending',
    },
  },
  { timestamps: true }
);

joinRequestSchema.index({ groupId: 1, userId: 1 }, { unique: true });

export const JoinRequest =
  mongoose.models.JoinRequest || mongoose.model('JoinRequest', joinRequestSchema);
