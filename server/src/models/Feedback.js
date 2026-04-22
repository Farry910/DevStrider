import mongoose from 'mongoose';

const feedbackSchema = new mongoose.Schema(
  {
    groupId: { type: mongoose.Schema.Types.ObjectId, ref: 'Group', required: true },
    userId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', required: true },
    category: {
      type: String,
      enum: ['general', 'bug', 'idea', 'other'],
      default: 'general',
    },
    message: { type: String, required: true, trim: true, maxlength: 8000 },
    pagePath: { type: String, trim: true, maxlength: 512, default: '' },
    status: {
      type: String,
      enum: ['open', 'resolved', 'ignored'],
      default: 'open',
    },
    ownerComment: { type: String, trim: true, maxlength: 4000, default: '' },
    ownerCommentAt: { type: Date, default: null },
  },
  { timestamps: true }
);

feedbackSchema.index({ groupId: 1, createdAt: -1 });

export const Feedback = mongoose.models.Feedback || mongoose.model('Feedback', feedbackSchema);
