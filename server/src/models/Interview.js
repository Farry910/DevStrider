import mongoose from 'mongoose';

export const INTERVIEW_TYPES = [
  'PHONE_SCREENING',
  'HR',
  'ASSESSMENT',
  'TECH_1',
  'TECH_2',
  'TECH_3',
  'CLIENT',
  'OFFER',
];
export const INTERVIEW_ORIGINS = ['linkedin_chat', 'bid'];

const interviewSchema = new mongoose.Schema(
  {
    userId: { type: mongoose.Schema.Types.ObjectId, ref: 'User', required: true, index: true },
    groupId: { type: mongoose.Schema.Types.ObjectId, ref: 'Group', required: true, index: true },
    meetingLink: { type: String, required: true, trim: true },
    origin: { type: String, enum: INTERVIEW_ORIGINS, required: true },
    bidId: { type: mongoose.Schema.Types.ObjectId, ref: 'UserBid', default: null },
    interviewType: { type: String, enum: INTERVIEW_TYPES, required: true },
    company: { type: String, default: '', trim: true },
    role: { type: String, default: '', trim: true },
    recruiter: { type: String, default: '', trim: true },
    additionalAttendees: { type: String, default: '' },
    scheduledDate: { type: Date, default: null },
    scheduledTime: { type: String, default: '' },
    durationMinutes: { type: Number, default: 60 },
    status: {
      type: String,
      enum: ['scheduled', 'completed', 'passed', 'failed', 'cancelled'],
      default: 'scheduled',
    },
    userComment: { type: String, default: '' },
    parentInterviewId: { type: mongoose.Schema.Types.ObjectId, ref: 'Interview', default: null },

    /**
     * Snapshot of the source bid's JD + resume content at the moment the interview was scheduled.
     * Stays stable for CALLERs even if the underlying bid is later edited or deleted; copied once
     * at create time, never re-fetched.
     */
    attachedJobDescription: { type: String, default: '' },
    attachedResumeContent: { type: String, default: '' },
    attachedAt: { type: Date, default: null },
  },
  { timestamps: true }
);

interviewSchema.index({ groupId: 1, userId: 1, scheduledDate: 1 });
interviewSchema.index({ groupId: 1, updatedAt: -1 });

export const Interview = mongoose.models.Interview || mongoose.model('Interview', interviewSchema);
