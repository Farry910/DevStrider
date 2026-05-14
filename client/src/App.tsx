import { lazy, Suspense } from 'react';
import { Navigate, Outlet, Route, Routes } from 'react-router-dom';
import { CircularProgress, Stack } from '@mui/material';
import { useAuth } from './auth/AuthContext';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import DashboardLayout from './pages/DashboardLayout';
import MyGroupsPage from './pages/MyGroupsPage';
import GroupLandingPage from './pages/GroupLandingPage';
import BidPanelPage from './pages/BidPanelPage';
import BidInterviewSchedulePage from './pages/BidInterviewSchedulePage';
import GroupJoinRequestsPage from './pages/GroupJoinRequestsPage';
import GroupSettingsPage from './pages/GroupSettingsPage';
import ProfileBadgesPage from './pages/ProfileBadgesPage';
import GroupProfileBadgeRequestsPage from './pages/GroupProfileBadgeRequestsPage';
import GroupFeedbackPage from './pages/GroupFeedbackPage';
import ProfileSettingsPage from './pages/ProfileSettingsPage';
const InterviewPanelPage = lazy(() => import('./pages/InterviewPanelPage'));
const StatsPage = lazy(() => import('./pages/StatsPage'));
const OverviewPage = lazy(() => import('./pages/OverviewPage'));

function PageFallback() {
  return (
    <Stack alignItems="center" justifyContent="center" minHeight="40vh" py={4}>
      <CircularProgress />
    </Stack>
  );
}

function PrivateRoute() {
  const { user, loading } = useAuth();
  if (loading) {
    return (
      <Stack alignItems="center" justifyContent="center" minHeight="60vh">
        <CircularProgress />
      </Stack>
    );
  }
  if (!user) return <Navigate to="/login" replace />;
  return <Outlet />;
}

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route element={<PrivateRoute />}>
        <Route path="/" element={<DashboardLayout />}>
          <Route index element={<MyGroupsPage />} />
          <Route path="profile" element={<ProfileSettingsPage />} />
          <Route path="g/:groupId" element={<GroupLandingPage />} />
          <Route path="g/:groupId/bids" element={<BidPanelPage />} />
          <Route
            path="g/:groupId/bids/schedule-interview"
            element={<BidInterviewSchedulePage />}
          />
          <Route
            path="g/:groupId/interviews"
            element={
              <Suspense fallback={<PageFallback />}>
                <InterviewPanelPage />
              </Suspense>
            }
          />
          <Route
            path="g/:groupId/stats"
            element={
              <Suspense fallback={<PageFallback />}>
                <StatsPage />
              </Suspense>
            }
          />
          <Route
            path="g/:groupId/overview"
            element={
              <Suspense fallback={<PageFallback />}>
                <OverviewPage />
              </Suspense>
            }
          />
          <Route path="g/:groupId/join-requests" element={<GroupJoinRequestsPage />} />
          <Route path="g/:groupId/profile-badges" element={<ProfileBadgesPage />} />
          <Route path="g/:groupId/badge-requests" element={<GroupProfileBadgeRequestsPage />} />
          <Route path="g/:groupId/settings" element={<GroupSettingsPage />} />
          <Route path="g/:groupId/feedback" element={<GroupFeedbackPage />} />
        </Route>
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
