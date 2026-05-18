import { useEffect, useMemo, useState } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AppBar,
  Avatar,
  Box,
  Button,
  Divider,
  Drawer,
  IconButton,
  List,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  ListSubheader,
  Toolbar,
  Tooltip,
  Typography,
} from '@mui/material';
import MenuIcon from '@mui/icons-material/Menu';
import ChevronLeftIcon from '@mui/icons-material/ChevronLeft';
import ChevronRightIcon from '@mui/icons-material/ChevronRight';
import GroupOutlinedIcon from '@mui/icons-material/GroupOutlined';
import AssignmentOutlinedIcon from '@mui/icons-material/AssignmentOutlined';
import EventOutlinedIcon from '@mui/icons-material/EventOutlined';
import CalendarMonthOutlinedIcon from '@mui/icons-material/CalendarMonthOutlined';
import BarChartOutlinedIcon from '@mui/icons-material/BarChartOutlined';
import TableRowsOutlinedIcon from '@mui/icons-material/TableRowsOutlined';
import PersonAddOutlinedIcon from '@mui/icons-material/PersonAddOutlined';
import SettingsOutlinedIcon from '@mui/icons-material/SettingsOutlined';
import FeedbackOutlinedIcon from '@mui/icons-material/FeedbackOutlined';
import WorkspacePremiumOutlinedIcon from '@mui/icons-material/WorkspacePremiumOutlined';
import VerifiedOutlinedIcon from '@mui/icons-material/VerifiedOutlined';
import WarningAmberOutlinedIcon from '@mui/icons-material/WarningAmberOutlined';
import PersonOutlineIcon from '@mui/icons-material/PersonOutline';
import AdminPanelSettingsOutlinedIcon from '@mui/icons-material/AdminPanelSettingsOutlined';
import { useAuth } from '../auth/AuthContext';
import { useGroupPresence } from '../hooks/useGroupPresence';
import api from '../api/client';
import { presetAvatarSrc } from '../avatarPresets';
import { ProfileAvatarDialog } from '../components/ProfileAvatarDialog';
import { NotificationBell } from '../components/NotificationBell';

const DRAWER_WIDTH_EXPANDED = 268;
const DRAWER_WIDTH_COLLAPSED = 72;
const SIDEBAR_COLLAPSED_KEY = 'devstrider:sidebar-collapsed';

type GroupMe = {
  group: {
    _id: string;
    name: string;
    locationKey: string;
    overviewScoreWeights?: Record<string, number> | null;
  };
  isMember: boolean;
  role: 'creator' | 'member' | 'none';
  removal?: {
    assisterUserId: string | null;
    ownerConfirmedAt: string | null;
    assisterConfirmedAt: string | null;
  };
};

function itemSx(active: boolean) {
  return {
    borderRadius: 1,
    mb: 0.25,
    bgcolor: active ? 'action.selected' : 'transparent',
    '&:hover': { bgcolor: active ? 'action.selected' : 'action.hover' },
  };
}

export default function DashboardLayout() {
  const { user, logout, applyUser } = useAuth();
  const nav = useNavigate();
  const qc = useQueryClient();
  const { pathname } = useLocation();
  const [mobileOpen, setMobileOpen] = useState(false);
  const [avatarDialogOpen, setAvatarDialogOpen] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => {
    try {
      return localStorage.getItem(SIDEBAR_COLLAPSED_KEY) === '1';
    } catch {
      return false;
    }
  });

  useEffect(() => {
    try {
      if (sidebarCollapsed) localStorage.setItem(SIDEBAR_COLLAPSED_KEY, '1');
      else localStorage.removeItem(SIDEBAR_COLLAPSED_KEY);
    } catch {
      /* ignore */
    }
  }, [sidebarCollapsed]);

  const groupId = useMemo(() => /^\/g\/([^/]+)/.exec(pathname)?.[1], [pathname]);

  const { data: groupMe, isLoading: groupMeLoading } = useQuery({
    queryKey: ['group', groupId, 'me'],
    enabled: !!groupId,
    queryFn: async () => (await api.get(`/groups/${groupId}/me`)).data as GroupMe,
  });

  const presenceUsers = useGroupPresence(
    groupId,
    Boolean(groupId && user && groupMe?.isMember)
  );

  const { data: pendingJoin } = useQuery({
    queryKey: ['group', groupId, 'pending-requests'],
    enabled: !!groupId && groupMe?.role === 'creator',
    queryFn: async () =>
      (await api.get(`/groups/${groupId}/pending-requests`)).data as { pending: { _id: string }[] },
  });

  const { data: pendingBadgeReq } = useQuery({
    queryKey: ['group', groupId, 'pending-profile-badge-requests'],
    enabled: !!groupId && groupMe?.role === 'creator',
    queryFn: async () =>
      (await api.get(`/groups/${groupId}/pending-profile-badge-requests`)).data as {
        pending: { id: string }[];
      },
  });

  const pendingJoinCount = pendingJoin?.pending?.length ?? 0;
  const pendingBadgeCount = pendingBadgeReq?.pending?.length ?? 0;

  const assisterRemovalPending =
    !!groupId &&
    !!user &&
    !!groupMe?.removal?.assisterUserId &&
    groupMe.removal.assisterUserId === user.id &&
    !!groupMe.removal.ownerConfirmedAt &&
    !groupMe.removal.assisterConfirmedAt;

  const assisterConfirmMut = useMutation({
    mutationFn: async () => api.post(`/groups/${groupId}/removal-request`),
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ['group', groupId] });
      qc.invalidateQueries({ queryKey: ['groups'] });
      if ((res.data as { completed?: boolean }).completed) {
        qc.removeQueries({ queryKey: ['group', groupId] });
        nav('/', { replace: true });
      }
    },
  });

  const renderDrawer = (collapsed: boolean, showCollapseToggle: boolean) => (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      {showCollapseToggle && (
        <Box
          sx={{
            flexShrink: 0,
            display: 'flex',
            justifyContent: collapsed ? 'center' : 'flex-end',
            alignItems: 'center',
            px: 0.5,
            py: 0.25,
            borderBottom: 1,
            borderColor: 'divider',
          }}
        >
          <IconButton
            size="small"
            onClick={() => setSidebarCollapsed((c) => !c)}
            aria-label={collapsed ? 'Expand navigation' : 'Collapse navigation'}
          >
            {collapsed ? <ChevronRightIcon /> : <ChevronLeftIcon />}
          </IconButton>
        </Box>
      )}
      <Box sx={{ overflow: 'auto', flex: 1, minHeight: 0, py: 1 }}>
        <List dense disablePadding sx={{ px: collapsed ? 0.5 : 1 }}>
          {!collapsed && (
            <ListSubheader disableSticky sx={{ bgcolor: 'transparent', lineHeight: '32px', fontWeight: 700 }}>
              Individual
            </ListSubheader>
          )}
          <Tooltip title="My groups" placement="right" disableHoverListener={!collapsed}>
            <ListItemButton
              component={NavLink}
              to="/"
              end
              onClick={() => setMobileOpen(false)}
              sx={{
                ...itemSx(pathname === '/' || pathname === ''),
                justifyContent: collapsed ? 'center' : undefined,
                px: collapsed ? 1 : undefined,
              }}
            >
              <ListItemIcon sx={{ minWidth: collapsed ? 0 : 36, justifyContent: 'center' }}>
                <GroupOutlinedIcon fontSize="small" />
              </ListItemIcon>
              {!collapsed && (
                <ListItemText primary="My groups" primaryTypographyProps={{ variant: 'body2' }} />
              )}
            </ListItemButton>
          </Tooltip>
          <Tooltip title="Profile & goals" placement="right" disableHoverListener={!collapsed}>
            <ListItemButton
              component={NavLink}
              to="/profile"
              onClick={() => setMobileOpen(false)}
              sx={{
                ...itemSx(pathname === '/profile'),
                justifyContent: collapsed ? 'center' : undefined,
                px: collapsed ? 1 : undefined,
              }}
            >
              <ListItemIcon sx={{ minWidth: collapsed ? 0 : 36, justifyContent: 'center' }}>
                <PersonOutlineIcon fontSize="small" />
              </ListItemIcon>
              {!collapsed && (
                <ListItemText primary="Profile & goals" primaryTypographyProps={{ variant: 'body2' }} />
              )}
            </ListItemButton>
          </Tooltip>
          {user?.platformRole === 'admin' && (
            <Tooltip title="Platform admin" placement="right" disableHoverListener={!collapsed}>
              <ListItemButton
                component={NavLink}
                to="/admin"
                onClick={() => setMobileOpen(false)}
                sx={{
                  ...itemSx(pathname === '/admin'),
                  justifyContent: collapsed ? 'center' : undefined,
                  px: collapsed ? 1 : undefined,
                }}
              >
                <ListItemIcon sx={{ minWidth: collapsed ? 0 : 36, justifyContent: 'center' }}>
                  <AdminPanelSettingsOutlinedIcon fontSize="small" />
                </ListItemIcon>
                {!collapsed && (
                  <ListItemText primary="Platform admin" primaryTypographyProps={{ variant: 'body2' }} />
                )}
              </ListItemButton>
            </Tooltip>
          )}
        </List>

        <Divider sx={{ my: collapsed ? 1 : 1.5, mx: collapsed ? 0.75 : 1 }} />

        <List dense disablePadding sx={{ px: collapsed ? 0.5 : 1 }}>
          {!collapsed && (
            <ListSubheader disableSticky sx={{ bgcolor: 'transparent', lineHeight: '32px', fontWeight: 700 }}>
              Group
            </ListSubheader>
          )}
          {!collapsed && !groupId && (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', px: 2, py: 0.5 }}>
              Open a group from My groups. Workspace links appear here for the active group.
            </Typography>
          )}
          {!collapsed && groupId && groupMe && !groupMe.isMember && pathname !== `/g/${groupId}` && (
            <Typography variant="caption" color="warning.main" sx={{ display: 'block', px: 2, py: 0.5 }}>
              You are not a member — some pages may be unavailable until you join.
            </Typography>
          )}
          {assisterRemovalPending && !collapsed && (
            <Alert
              severity="warning"
              sx={{ mx: 1, mb: 1 }}
              action={
                <Button
                  color="inherit"
                  size="small"
                  disabled={assisterConfirmMut.isPending}
                  onClick={() => {
                    if (
                      !window.confirm(
                        `The owner requested to delete ${groupMe?.group?.name ?? 'this group'}. Confirm deletion? This cannot be undone.`
                      )
                    ) {
                      return;
                    }
                    assisterConfirmMut.mutate();
                  }}
                >
                  Confirm deletion
                </Button>
              }
            >
              Removal assister: the owner started group deletion. Your confirmation is required to finish.
            </Alert>
          )}
          {assisterRemovalPending && collapsed && (
            <Tooltip
              title="Removal assister: confirm group deletion (open sidebar for actions)"
              placement="right"
            >
              <Box sx={{ display: 'flex', justifyContent: 'center', py: 0.5 }}>
                <IconButton
                  size="small"
                  color="warning"
                  aria-label="Group deletion needs your confirmation"
                  onClick={() => setSidebarCollapsed(false)}
                >
                  <WarningAmberOutlinedIcon fontSize="small" />
                </IconButton>
              </Box>
            </Tooltip>
          )}
        {groupId && (
          <>
            {!collapsed && (
              <Typography
                variant="caption"
                color="text.secondary"
                sx={{ display: 'block', px: 2, pb: 0.75 }}
                noWrap
                title={groupMe?.group?.name}
              >
                {groupMeLoading ? '…' : groupMe?.group?.name ?? 'Group'}
              </Typography>
            )}
            <Tooltip title="Bid board" placement="right" disableHoverListener={!collapsed}>
              <ListItemButton
                component={NavLink}
                to={`/g/${groupId}/bids`}
                onClick={() => setMobileOpen(false)}
                sx={{
                  ...itemSx(pathname === `/g/${groupId}/bids`),
                  justifyContent: collapsed ? 'center' : undefined,
                  px: collapsed ? 1 : undefined,
                }}
              >
                <ListItemIcon sx={{ minWidth: collapsed ? 0 : 36, justifyContent: 'center' }}>
                  <AssignmentOutlinedIcon fontSize="small" />
                </ListItemIcon>
                {!collapsed && (
                  <ListItemText primary="Bid board" primaryTypographyProps={{ variant: 'body2' }} />
                )}
              </ListItemButton>
            </Tooltip>
            <Tooltip title="Schedule from bids" placement="right" disableHoverListener={!collapsed}>
              <ListItemButton
                component={NavLink}
                to={`/g/${groupId}/bids/schedule-interview`}
                onClick={() => setMobileOpen(false)}
                sx={{
                  ...itemSx(pathname === `/g/${groupId}/bids/schedule-interview`),
                  justifyContent: collapsed ? 'center' : undefined,
                  px: collapsed ? 1 : undefined,
                }}
              >
                <ListItemIcon sx={{ minWidth: collapsed ? 0 : 36, justifyContent: 'center' }}>
                  <CalendarMonthOutlinedIcon fontSize="small" />
                </ListItemIcon>
                {!collapsed && (
                  <ListItemText
                    primary="Schedule from bids"
                    primaryTypographyProps={{ variant: 'body2' }}
                  />
                )}
              </ListItemButton>
            </Tooltip>
            <Tooltip title="Interviews" placement="right" disableHoverListener={!collapsed}>
              <ListItemButton
                component={NavLink}
                to={`/g/${groupId}/interviews`}
                onClick={() => setMobileOpen(false)}
                sx={{
                  ...itemSx(pathname === `/g/${groupId}/interviews`),
                  justifyContent: collapsed ? 'center' : undefined,
                  px: collapsed ? 1 : undefined,
                }}
              >
                <ListItemIcon sx={{ minWidth: collapsed ? 0 : 36, justifyContent: 'center' }}>
                  <EventOutlinedIcon fontSize="small" />
                </ListItemIcon>
                {!collapsed && (
                  <ListItemText primary="Interviews" primaryTypographyProps={{ variant: 'body2' }} />
                )}
              </ListItemButton>
            </Tooltip>
            <Tooltip title="Statistics" placement="right" disableHoverListener={!collapsed}>
              <ListItemButton
                component={NavLink}
                to={`/g/${groupId}/stats`}
                onClick={() => setMobileOpen(false)}
                sx={{
                  ...itemSx(pathname === `/g/${groupId}/stats`),
                  justifyContent: collapsed ? 'center' : undefined,
                  px: collapsed ? 1 : undefined,
                }}
              >
                <ListItemIcon sx={{ minWidth: collapsed ? 0 : 36, justifyContent: 'center' }}>
                  <BarChartOutlinedIcon fontSize="small" />
                </ListItemIcon>
                {!collapsed && (
                  <ListItemText primary="Statistics" primaryTypographyProps={{ variant: 'body2' }} />
                )}
              </ListItemButton>
            </Tooltip>
            <Tooltip title="Overview" placement="right" disableHoverListener={!collapsed}>
              <ListItemButton
                component={NavLink}
                to={`/g/${groupId}/overview`}
                onClick={() => setMobileOpen(false)}
                sx={{
                  ...itemSx(pathname === `/g/${groupId}/overview`),
                  justifyContent: collapsed ? 'center' : undefined,
                  px: collapsed ? 1 : undefined,
                }}
              >
                <ListItemIcon sx={{ minWidth: collapsed ? 0 : 36, justifyContent: 'center' }}>
                  <TableRowsOutlinedIcon fontSize="small" />
                </ListItemIcon>
                {!collapsed && (
                  <ListItemText primary="Overview" primaryTypographyProps={{ variant: 'body2' }} />
                )}
              </ListItemButton>
            </Tooltip>
            {groupMe?.isMember && (
              <>
                <Tooltip title="Feedback" placement="right" disableHoverListener={!collapsed}>
                  <ListItemButton
                    component={NavLink}
                    to={`/g/${groupId}/feedback`}
                    onClick={() => setMobileOpen(false)}
                    sx={{
                      ...itemSx(pathname === `/g/${groupId}/feedback`),
                      justifyContent: collapsed ? 'center' : undefined,
                      px: collapsed ? 1 : undefined,
                    }}
                  >
                    <ListItemIcon sx={{ minWidth: collapsed ? 0 : 36, justifyContent: 'center' }}>
                      <FeedbackOutlinedIcon fontSize="small" />
                    </ListItemIcon>
                    {!collapsed && (
                      <ListItemText primary="Feedback" primaryTypographyProps={{ variant: 'body2' }} />
                    )}
                  </ListItemButton>
                </Tooltip>
                <Tooltip title="Profile badges" placement="right" disableHoverListener={!collapsed}>
                  <ListItemButton
                    component={NavLink}
                    to={`/g/${groupId}/profile-badges`}
                    onClick={() => setMobileOpen(false)}
                    sx={{
                      ...itemSx(pathname === `/g/${groupId}/profile-badges`),
                      justifyContent: collapsed ? 'center' : undefined,
                      px: collapsed ? 1 : undefined,
                    }}
                  >
                    <ListItemIcon sx={{ minWidth: collapsed ? 0 : 36, justifyContent: 'center' }}>
                      <WorkspacePremiumOutlinedIcon fontSize="small" />
                    </ListItemIcon>
                    {!collapsed && (
                      <ListItemText primary="Profile badges" primaryTypographyProps={{ variant: 'body2' }} />
                    )}
                  </ListItemButton>
                </Tooltip>
              </>
            )}
            {groupMe?.role === 'creator' && (
              <>
                <Tooltip
                  title={
                    collapsed
                      ? `Badge requests${pendingBadgeCount > 0 ? ` (${pendingBadgeCount} pending)` : ''}`
                      : ''
                  }
                  placement="right"
                  disableHoverListener={!collapsed}
                >
                  <ListItemButton
                    component={NavLink}
                    to={`/g/${groupId}/badge-requests`}
                    onClick={() => setMobileOpen(false)}
                    sx={{
                      ...itemSx(pathname === `/g/${groupId}/badge-requests`),
                      justifyContent: collapsed ? 'center' : undefined,
                      px: collapsed ? 1 : undefined,
                    }}
                  >
                    <ListItemIcon sx={{ minWidth: collapsed ? 0 : 36, justifyContent: 'center' }}>
                      <VerifiedOutlinedIcon fontSize="small" />
                    </ListItemIcon>
                    {!collapsed && (
                      <ListItemText
                        primary="Badge requests"
                        secondary={pendingBadgeCount > 0 ? `${pendingBadgeCount} pending` : undefined}
                        primaryTypographyProps={{ variant: 'body2' }}
                        secondaryTypographyProps={{ variant: 'caption' }}
                      />
                    )}
                  </ListItemButton>
                </Tooltip>
                <Tooltip
                  title={
                    collapsed
                      ? `Join requests${pendingJoinCount > 0 ? ` (${pendingJoinCount} pending)` : ''}`
                      : ''
                  }
                  placement="right"
                  disableHoverListener={!collapsed}
                >
                  <ListItemButton
                    component={NavLink}
                    to={`/g/${groupId}/join-requests`}
                    onClick={() => setMobileOpen(false)}
                    sx={{
                      ...itemSx(pathname === `/g/${groupId}/join-requests`),
                      justifyContent: collapsed ? 'center' : undefined,
                      px: collapsed ? 1 : undefined,
                    }}
                  >
                    <ListItemIcon sx={{ minWidth: collapsed ? 0 : 36, justifyContent: 'center' }}>
                      <PersonAddOutlinedIcon fontSize="small" />
                    </ListItemIcon>
                    {!collapsed && (
                      <ListItemText
                        primary="Join requests"
                        secondary={pendingJoinCount > 0 ? `${pendingJoinCount} pending` : undefined}
                        primaryTypographyProps={{ variant: 'body2' }}
                        secondaryTypographyProps={{ variant: 'caption' }}
                      />
                    )}
                  </ListItemButton>
                </Tooltip>
                <Tooltip title="Group settings" placement="right" disableHoverListener={!collapsed}>
                  <ListItemButton
                    component={NavLink}
                    to={`/g/${groupId}/settings`}
                    onClick={() => setMobileOpen(false)}
                    sx={{
                      ...itemSx(pathname === `/g/${groupId}/settings`),
                      justifyContent: collapsed ? 'center' : undefined,
                      px: collapsed ? 1 : undefined,
                    }}
                  >
                    <ListItemIcon sx={{ minWidth: collapsed ? 0 : 36, justifyContent: 'center' }}>
                      <SettingsOutlinedIcon fontSize="small" />
                    </ListItemIcon>
                    {!collapsed && (
                      <ListItemText primary="Group settings" primaryTypographyProps={{ variant: 'body2' }} />
                    )}
                  </ListItemButton>
                </Tooltip>
              </>
            )}
          </>
        )}
      </List>
      </Box>
    </Box>
  );

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', minHeight: '100vh', bgcolor: 'background.default' }}>
      <AppBar
        position="fixed"
        color="inherit"
        elevation={0}
        sx={{
          zIndex: (t) => t.zIndex.drawer + 1,
          borderBottom: 1,
          borderColor: 'divider',
        }}
      >
        <Toolbar sx={{ gap: 1 }}>
          <IconButton
            color="inherit"
            edge="start"
            onClick={() => setMobileOpen(true)}
            sx={{ mr: 0.5, display: { sm: 'none' } }}
            aria-label="open navigation"
          >
            <MenuIcon />
          </IconButton>
          <Typography
            variant="h6"
            component="button"
            type="button"
            onClick={() => nav('/')}
            sx={{
              cursor: 'pointer',
              border: 'none',
              background: 'none',
              font: 'inherit',
              color: 'inherit',
              textAlign: 'left',
              flexGrow: 1,
            }}
          >
            DevStrider
          </Typography>
          {groupId && groupMe?.isMember && presenceUsers.length > 0 && (
            <Tooltip
              title={`Active in this group: ${presenceUsers.map((u) => u.nickname || u.id).join(', ')}`}
            >
              <Box
                aria-label="Members online in this group"
                sx={{
                  display: 'flex',
                  flexDirection: 'row',
                  alignItems: 'center',
                  flexShrink: 1,
                  minWidth: 0,
                  maxWidth: { xs: 120, sm: 220 },
                  pr: 0.5,
                }}
              >
                {presenceUsers.slice(0, 10).map((u, i) => (
                  <Avatar
                    key={u.id}
                    src={presetAvatarSrc(u.avatarId) ?? undefined}
                    sx={{
                      width: 28,
                      height: 28,
                      fontSize: '0.75rem',
                      ml: i > 0 ? -1 : 0,
                      border: 2,
                      borderColor: 'background.paper',
                      boxSizing: 'content-box',
                    }}
                  >
                    {(u.nickname || '?').trim().charAt(0).toUpperCase() || '?'}
                  </Avatar>
                ))}
                {presenceUsers.length > 10 && (
                  <Avatar
                    sx={{
                      width: 28,
                      height: 28,
                      fontSize: '0.65rem',
                      ml: -1,
                      border: 2,
                      borderColor: 'background.paper',
                      boxSizing: 'content-box',
                      bgcolor: 'action.selected',
                    }}
                  >
                    +{presenceUsers.length - 10}
                  </Avatar>
                )}
              </Box>
            </Tooltip>
          )}
          {user && <NotificationBell />}
          {user && (
            <>
              <IconButton
                size="small"
                onClick={() => setAvatarDialogOpen(true)}
                aria-label="Choose profile picture for link badges"
                sx={{ p: 0.25 }}
              >
                <Avatar
                  src={presetAvatarSrc(user.avatarId) ?? undefined}
                  sx={{ width: 32, height: 32, fontSize: '0.9rem', bgcolor: 'primary.dark' }}
                >
                  {user.nickname.trim().charAt(0).toUpperCase() || '?'}
                </Avatar>
              </IconButton>
              <Typography variant="body2" color="text.secondary" noWrap sx={{ maxWidth: { xs: 80, sm: 160 } }}>
                {user.nickname}
              </Typography>
            </>
          )}
          <Button color="inherit" variant="outlined" size="small" onClick={() => logout()}>
            Log out
          </Button>
        </Toolbar>
      </AppBar>
      <Toolbar />

      <Box sx={{ display: 'flex', flex: 1, minHeight: 0 }}>
        <Drawer
          variant="temporary"
          open={mobileOpen}
          onClose={() => setMobileOpen(false)}
          ModalProps={{ keepMounted: true }}
          sx={{
            display: { xs: 'block', sm: 'none' },
            '& .MuiDrawer-paper': {
              width: DRAWER_WIDTH_EXPANDED,
              boxSizing: 'border-box',
            },
          }}
        >
          {renderDrawer(false, false)}
        </Drawer>
        <Drawer
          variant="permanent"
          sx={{
            display: { xs: 'none', sm: 'block' },
            width: sidebarCollapsed ? DRAWER_WIDTH_COLLAPSED : DRAWER_WIDTH_EXPANDED,
            flexShrink: 0,
            transition: (theme) =>
              theme.transitions.create('width', {
                easing: theme.transitions.easing.sharp,
                duration: theme.transitions.duration.enteringScreen,
              }),
            '& .MuiDrawer-paper': {
              width: sidebarCollapsed ? DRAWER_WIDTH_COLLAPSED : DRAWER_WIDTH_EXPANDED,
              boxSizing: 'border-box',
              position: 'sticky',
              top: 0,
              alignSelf: 'flex-start',
              maxHeight: 'calc(100dvh - 64px)',
              overflowX: 'hidden',
              display: 'flex',
              flexDirection: 'column',
              borderRight: 1,
              borderColor: 'divider',
              transition: (theme) =>
                theme.transitions.create('width', {
                  easing: theme.transitions.easing.sharp,
                  duration: theme.transitions.duration.enteringScreen,
                }),
            },
          }}
          open
        >
          {renderDrawer(sidebarCollapsed, true)}
        </Drawer>

        <Box
          component="main"
          sx={{
            flexGrow: 1,
            minWidth: 0,
            overflow: 'auto',
            px: { xs: 1.5, sm: 2.5 },
            py: 2,
          }}
        >
          <Outlet />
        </Box>
      </Box>
      {user && (
        <ProfileAvatarDialog
          open={avatarDialogOpen}
          onClose={() => setAvatarDialogOpen(false)}
          nickname={user.nickname}
          avatarId={user.avatarId}
          onSaved={applyUser}
        />
      )}
    </Box>
  );
}
