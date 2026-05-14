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
