import api from './client';

export type Education = {
  degree: string;
  school: string;
  location: string;
  startYear: number | null;
  endYear: number | null;
};

export type Certification = {
  name: string;
  issuer: string;
  year: number | null;
};

/**
 * Per-group resume experience entry. Role/title is NOT stored here — it comes from the bid
 * body's `[Subtitle N]` placeholder so each bid can tune the title per posting. Years are
 * optional individually so partial periods ("2022 -" current, "- 2020" legacy) render gracefully.
 */
export type Experience = {
  company: string;
  location: string;
  startYear: number | null;
  endYear: number | null;
};

/**
 * Per-group resume profile — the source of truth the resume composer pulls from. Same field set
 * as the user-level Profile minus identity/account fields (email, nickname, avatar, timezone,
 * goals, leaderboard opt-in), plus experiences[].
 */
export type ResumeProfile = {
  displayName: string;
  headline: string;
  location: string;
  phone: string;
  personalEmail: string;
  linkedinUrl: string;
  education: Education[];
  certifications: Certification[];
  experiences: Experience[];
};

export type Goals = {
  bidsPerDay: number;
  interviewsPerWeek: number;
  offersPerMonth: number;
};

export type Profile = {
  id: string;
  email: string;
  nickname: string;
  avatarId: string;
  displayName: string;
  headline: string;
  location: string;
  phone: string;
  personalEmail: string;
  linkedinUrl: string;
  /** IANA timezone identifier; defaults to 'UTC' until the user sets it. */
  timezone: string;
  education: Education[];
  certifications: Certification[];
  goals: Goals;
  showOnLeaderboard: boolean;
};

export async function getMyProfile(): Promise<Profile> {
  const { data } = await api.get<{ profile: Profile }>('/profile/me');
  return data.profile;
}

export async function patchMyProfile(patch: Partial<Profile>): Promise<Profile> {
  const { data } = await api.patch<{ profile: Profile }>('/profile/me', patch);
  return data.profile;
}

export async function patchMyGoals(goals: Partial<Goals>): Promise<Goals> {
  const { data } = await api.patch<{ goals: Goals }>('/profile/me/goals', goals);
  return data.goals;
}

/**
 * Fetch the current user's per-group profile for the given group. The server lazily seeds the
 * record from the user's top-level profile on first read so existing users don't lose data.
 */
export async function getGroupProfile(groupId: string): Promise<ResumeProfile> {
  const { data } = await api.get<{ profile: ResumeProfile }>(`/groups/${groupId}/profile/me`);
  return data.profile;
}

export async function patchGroupProfile(
  groupId: string,
  patch: Partial<ResumeProfile>
): Promise<ResumeProfile> {
  const { data } = await api.patch<{ profile: ResumeProfile }>(
    `/groups/${groupId}/profile/me`,
    patch
  );
  return data.profile;
}

export type NotificationItem = {
  id: string;
  kind: string;
  payload: Record<string, unknown> | null;
  readAt: string | null;
  createdAt: string;
};

export async function getNotifications(unreadOnly = false): Promise<{
  notifications: NotificationItem[];
  unreadCount: number;
}> {
  const { data } = await api.get<{ notifications: NotificationItem[]; unreadCount: number }>(
    '/notifications/me',
    { params: { unreadOnly: unreadOnly ? 'true' : 'false', limit: 50 } }
  );
  return data;
}

export async function markNotificationRead(id: string): Promise<void> {
  await api.post(`/notifications/${id}/read`);
}

export async function markAllNotificationsRead(): Promise<void> {
  await api.post('/notifications/read-all');
}

export async function approveRoleRequest(
  groupId: string,
  notificationId: string
): Promise<{ ok: true; roles: string[]; userId: string }> {
  const { data } = await api.post<{ ok: true; roles: string[]; userId: string }>(
    `/groups/${groupId}/role-requests/${notificationId}/approve`
  );
  return data;
}

export async function denyRoleRequest(groupId: string, notificationId: string): Promise<void> {
  await api.post(`/groups/${groupId}/role-requests/${notificationId}/deny`);
}

export type AchievementProgress = {
  goals: Goals;
  progress: {
    daily_bids: { value: number; target: number };
    weekly_interviews: { value: number; target: number };
    monthly_offers: { value: number; target: number };
  };
  activeBadges: Array<{
    kind: 'daily_bids' | 'weekly_interviews' | 'monthly_offers';
    periodKey: string;
    achievedAt: string;
    metricValue: number;
    target: number;
  }>;
};

export async function getMyAchievements(groupId: string): Promise<AchievementProgress> {
  const { data } = await api.get<AchievementProgress>(`/groups/${groupId}/achievements/me`);
  return data;
}

export type LeaderboardRow = {
  userId: string;
  nickname: string;
  avatarId: string;
  score: number;
  rank: number;
  isCaller: boolean;
  anonymous: boolean;
};

export async function getLeaderboard(groupId: string): Promise<{
  rows: LeaderboardRow[];
  window: { from: string; to: string };
}> {
  const { data } = await api.get<{
    rows: LeaderboardRow[];
    weights: unknown;
    window: { from: string; to: string };
  }>(`/groups/${groupId}/leaderboard`);
  return { rows: data.rows, window: data.window };
}
