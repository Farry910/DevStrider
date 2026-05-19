import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  FormControlLabel,
  IconButton,
  LinearProgress,
  Paper,
  Stack,
  Switch,
  TextField,
  Typography,
} from '@mui/material';
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline';
import AddIcon from '@mui/icons-material/Add';
import {
  getMyProfile,
  patchMyProfile,
  patchMyGoals,
  type Profile,
  type Education,
  type Certification,
  type Goals,
} from '../api/profile';

function blankEdu(): Education {
  return { degree: '', school: '', location: '', startYear: null, endYear: null };
}
function blankCert(): Certification {
  return { name: '', issuer: '', year: null };
}

export default function ProfileSettingsPage() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['profile', 'me'] as const, queryFn: getMyProfile });

  const [form, setForm] = useState<Profile | null>(null);

  useEffect(() => {
    if (q.data) setForm(q.data);
  }, [q.data]);

  const profileMut = useMutation({
    mutationFn: async (p: Partial<Profile>) => patchMyProfile(p),
    onSuccess: (data) => {
      qc.setQueryData(['profile', 'me'], data);
      setForm(data);
    },
  });

  const goalsMut = useMutation({
    mutationFn: async (g: Partial<Goals>) => patchMyGoals(g),
    onSuccess: (goals) => {
      qc.setQueryData(['profile', 'me'], (prev: Profile | undefined) =>
        prev ? { ...prev, goals } : prev
      );
      setForm((prev) => (prev ? { ...prev, goals } : prev));
    },
  });

  if (q.isLoading) return <LinearProgress />;
  if (q.isError || !form) return <Alert severity="error">Could not load profile.</Alert>;

  function update<K extends keyof Profile>(key: K, value: Profile[K]) {
    setForm((prev) => (prev ? { ...prev, [key]: value } : prev));
  }

  function setEducation(arr: Education[]) {
    setForm((prev) => (prev ? { ...prev, education: arr } : prev));
  }
  function setCertifications(arr: Certification[]) {
    setForm((prev) => (prev ? { ...prev, certifications: arr } : prev));
  }

  return (
    <Stack spacing={3} maxWidth={720}>
      <Box>
        <Typography variant="h5">Profile</Typography>
        <Typography variant="body2" color="text.secondary">
          These details are used to compose your resume on the bid board. Only you can see them — teammates only see the body of your applied bids, never your contact info.
        </Typography>
      </Box>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="subtitle1" gutterBottom>
          Personal info
        </Typography>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <TextField
            label="Display name"
            value={form.displayName}
            onChange={(e) => update('displayName', e.target.value)}
            fullWidth
            size="small"
          />
          <TextField
            label="Headline (e.g. Senior Full Stack Developer)"
            value={form.headline}
            onChange={(e) => update('headline', e.target.value)}
            fullWidth
            size="small"
          />
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
            <TextField
              label="Location"
              value={form.location}
              onChange={(e) => update('location', e.target.value)}
              fullWidth
              size="small"
            />
            <TextField
              label="Phone"
              value={form.phone}
              onChange={(e) => update('phone', e.target.value)}
              fullWidth
              size="small"
            />
          </Stack>
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
            <TextField
              label="Personal email (resume contact)"
              value={form.personalEmail}
              onChange={(e) => update('personalEmail', e.target.value)}
              fullWidth
              size="small"
              type="email"
            />
            <TextField
              label="LinkedIn URL"
              value={form.linkedinUrl}
              onChange={(e) => update('linkedinUrl', e.target.value)}
              fullWidth
              size="small"
            />
          </Stack>
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} alignItems={{ sm: 'center' }}>
            <TextField
              label="Timezone (IANA, e.g. America/Mexico_City)"
              value={form.timezone}
              onChange={(e) => update('timezone', e.target.value)}
              fullWidth
              size="small"
              helperText="Used to display times in your local zone. Storage stays UTC."
            />
            <Button
              size="small"
              variant="outlined"
              onClick={() => {
                /** Best-effort browser detection: Intl.DateTimeFormat resolves to the OS zone. */
                const detected = Intl.DateTimeFormat().resolvedOptions().timeZone;
                if (detected) update('timezone', detected);
              }}
              sx={{ flexShrink: 0, alignSelf: { xs: 'flex-start', sm: 'auto' } }}
            >
              Detect
            </Button>
          </Stack>
          <FormControlLabel
            control={
              <Switch
                checked={form.showOnLeaderboard}
                onChange={(_, c) => update('showOnLeaderboard', c)}
                size="small"
              />
            }
            label={
              <Typography variant="body2">
                Show my name on the group leaderboard (off = appear as anonymous to teammates; you still see your own row)
              </Typography>
            }
          />
        </Stack>
      </Paper>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1 }}>
          <Typography variant="subtitle1">Education</Typography>
          <Button
            size="small"
            startIcon={<AddIcon />}
            onClick={() => setEducation([...(form.education ?? []), blankEdu()])}
          >
            Add
          </Button>
        </Stack>
        <Stack spacing={2}>
          {form.education.map((e, i) => (
            <Box key={i} sx={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr 80px 80px 36px', gap: 1, alignItems: 'center' }}>
              <TextField
                size="small"
                label="Degree"
                value={e.degree}
                onChange={(ev) => {
                  const arr = [...form.education];
                  arr[i] = { ...e, degree: ev.target.value };
                  setEducation(arr);
                }}
              />
              <TextField
                size="small"
                label="School"
                value={e.school}
                onChange={(ev) => {
                  const arr = [...form.education];
                  arr[i] = { ...e, school: ev.target.value };
                  setEducation(arr);
                }}
              />
              <TextField
                size="small"
                label="Location"
                value={e.location}
                onChange={(ev) => {
                  const arr = [...form.education];
                  arr[i] = { ...e, location: ev.target.value };
                  setEducation(arr);
                }}
              />
              <TextField
                size="small"
                label="From"
                type="number"
                value={e.startYear ?? ''}
                onChange={(ev) => {
                  const arr = [...form.education];
                  const v = ev.target.value;
                  arr[i] = { ...e, startYear: v ? Number(v) : null };
                  setEducation(arr);
                }}
              />
              <TextField
                size="small"
                label="To"
                type="number"
                value={e.endYear ?? ''}
                onChange={(ev) => {
                  const arr = [...form.education];
                  const v = ev.target.value;
                  arr[i] = { ...e, endYear: v ? Number(v) : null };
                  setEducation(arr);
                }}
              />
              <IconButton
                size="small"
                aria-label="Remove education"
                onClick={() => setEducation(form.education.filter((_, j) => j !== i))}
              >
                <DeleteOutlineIcon fontSize="small" />
              </IconButton>
            </Box>
          ))}
          {form.education.length === 0 && (
            <Typography variant="caption" color="text.secondary">
              No education added yet.
            </Typography>
          )}
        </Stack>
      </Paper>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1 }}>
          <Typography variant="subtitle1">Certifications</Typography>
          <Button
            size="small"
            startIcon={<AddIcon />}
            onClick={() => setCertifications([...(form.certifications ?? []), blankCert()])}
          >
            Add
          </Button>
        </Stack>
        <Stack spacing={2}>
          {form.certifications.map((c, i) => (
            <Box key={i} sx={{ display: 'grid', gridTemplateColumns: '1fr 1fr 80px 36px', gap: 1, alignItems: 'center' }}>
              <TextField
                size="small"
                label="Name"
                value={c.name}
                onChange={(ev) => {
                  const arr = [...form.certifications];
                  arr[i] = { ...c, name: ev.target.value };
                  setCertifications(arr);
                }}
              />
              <TextField
                size="small"
                label="Issuer"
                value={c.issuer}
                onChange={(ev) => {
                  const arr = [...form.certifications];
                  arr[i] = { ...c, issuer: ev.target.value };
                  setCertifications(arr);
                }}
              />
              <TextField
                size="small"
                label="Year"
                type="number"
                value={c.year ?? ''}
                onChange={(ev) => {
                  const arr = [...form.certifications];
                  const v = ev.target.value;
                  arr[i] = { ...c, year: v ? Number(v) : null };
                  setCertifications(arr);
                }}
              />
              <IconButton
                size="small"
                aria-label="Remove certification"
                onClick={() => setCertifications(form.certifications.filter((_, j) => j !== i))}
              >
                <DeleteOutlineIcon fontSize="small" />
              </IconButton>
            </Box>
          ))}
          {form.certifications.length === 0 && (
            <Typography variant="caption" color="text.secondary">
              No certifications added yet.
            </Typography>
          )}
        </Stack>
      </Paper>

      <Box>
        <Button
          variant="contained"
          disabled={profileMut.isPending}
          onClick={() =>
            profileMut.mutate({
              displayName: form.displayName,
              headline: form.headline,
              location: form.location,
              phone: form.phone,
              personalEmail: form.personalEmail,
              linkedinUrl: form.linkedinUrl,
              timezone: form.timezone,
              education: form.education,
              certifications: form.certifications,
              showOnLeaderboard: form.showOnLeaderboard,
            })
          }
        >
          {profileMut.isPending ? 'Saving…' : 'Save profile'}
        </Button>
        {profileMut.isError && (
          <Alert severity="error" sx={{ mt: 1 }}>
            Could not save profile.
          </Alert>
        )}
        {profileMut.isSuccess && !profileMut.isPending && (
          <Typography variant="caption" color="success.main" sx={{ ml: 2 }}>
            Saved.
          </Typography>
        )}
      </Box>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="subtitle1" gutterBottom>
          Daily / weekly / monthly goals
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Set 0 to disable a metric. Counted against UTC day / rolling 7 days / UTC month. When you hit a goal you'll get a notification and a temporary badge on your avatar.
        </Typography>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
          <TextField
            label="Bids per day (applied)"
            type="number"
            value={form.goals.bidsPerDay}
            onChange={(e) => update('goals', { ...form.goals, bidsPerDay: Number(e.target.value) })}
            fullWidth
            size="small"
            inputProps={{ min: 0, max: 1000 }}
          />
          <TextField
            label="Interviews per week"
            type="number"
            value={form.goals.interviewsPerWeek}
            onChange={(e) => update('goals', { ...form.goals, interviewsPerWeek: Number(e.target.value) })}
            fullWidth
            size="small"
            inputProps={{ min: 0, max: 1000 }}
          />
          <TextField
            label="Offers per month"
            type="number"
            value={form.goals.offersPerMonth}
            onChange={(e) => update('goals', { ...form.goals, offersPerMonth: Number(e.target.value) })}
            fullWidth
            size="small"
            inputProps={{ min: 0, max: 1000 }}
          />
        </Stack>
        <Button
          variant="outlined"
          sx={{ mt: 2 }}
          disabled={goalsMut.isPending}
          onClick={() => goalsMut.mutate(form.goals)}
        >
          {goalsMut.isPending ? 'Saving…' : 'Save goals'}
        </Button>
        {goalsMut.isError && (
          <Alert severity="error" sx={{ mt: 1 }}>
            Could not save goals.
          </Alert>
        )}
      </Paper>
    </Stack>
  );
}
