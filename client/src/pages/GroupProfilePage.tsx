import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  IconButton,
  LinearProgress,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import AddIcon from '@mui/icons-material/Add';
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline';
import {
  getGroupProfile,
  patchGroupProfile,
  type Certification,
  type Education,
  type Experience,
  type ResumeProfile,
} from '../api/profile';

function blankEdu(): Education {
  return { degree: '', school: '', location: '', startYear: null, endYear: null };
}
function blankCert(): Certification {
  return { name: '', issuer: '', year: null };
}
function blankExp(): Experience {
  return { company: '', location: '', startYear: null, endYear: null };
}

export default function GroupProfilePage() {
  const { groupId = '' } = useParams();
  const qc = useQueryClient();
  const q = useQuery({
    queryKey: ['groupProfile', 'me', groupId] as const,
    queryFn: () => getGroupProfile(groupId),
    enabled: Boolean(groupId),
  });
  const [form, setForm] = useState<ResumeProfile | null>(null);

  useEffect(() => {
    if (q.data) setForm(q.data);
  }, [q.data]);

  const mut = useMutation({
    mutationFn: async (p: Partial<ResumeProfile>) => patchGroupProfile(groupId, p),
    onSuccess: (data) => {
      qc.setQueryData(['groupProfile', 'me', groupId], data);
      setForm(data);
    },
  });

  if (q.isLoading) return <LinearProgress />;
  if (q.isError || !form) return <Alert severity="error">Could not load group profile.</Alert>;

  function update<K extends keyof ResumeProfile>(key: K, value: ResumeProfile[K]) {
    setForm((prev) => (prev ? { ...prev, [key]: value } : prev));
  }

  return (
    <Stack spacing={3} maxWidth={820}>
      <Box>
        <Typography variant="h5">Resume profile (this group)</Typography>
        <Typography variant="body2" color="text.secondary">
          These details feed the resume composer for bids in this group. Tailor a different resume
          per group — your top-level profile is the seed, but edits here only affect this group.
        </Typography>
      </Box>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="subtitle1" gutterBottom>
          Header
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
              label="Personal email"
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
        </Stack>
      </Paper>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1 }}>
          <Box>
            <Typography variant="subtitle1">Experiences</Typography>
            <Typography variant="caption" color="text.secondary">
              Substitutes [Experience 1], [Experience 2], … in the resume body. The role/title
              for each comes from the body's [Subtitle N] line — only company, location, and
              years live here. Order matters.
            </Typography>
          </Box>
          <Button
            size="small"
            startIcon={<AddIcon />}
            onClick={() => update('experiences', [...form.experiences, blankExp()])}
          >
            Add
          </Button>
        </Stack>
        <Stack spacing={2}>
          {form.experiences.map((x, i) => (
            <Box
              key={i}
              sx={{
                display: 'grid',
                gridTemplateColumns: '1fr 1fr 80px 80px 36px',
                gap: 1,
                alignItems: 'center',
              }}
            >
              <TextField
                size="small"
                label="Company"
                value={x.company}
                onChange={(ev) => {
                  const arr = [...form.experiences];
                  arr[i] = { ...x, company: ev.target.value };
                  update('experiences', arr);
                }}
              />
              <TextField
                size="small"
                label="Location"
                value={x.location}
                onChange={(ev) => {
                  const arr = [...form.experiences];
                  arr[i] = { ...x, location: ev.target.value };
                  update('experiences', arr);
                }}
              />
              <TextField
                size="small"
                label="From"
                type="number"
                value={x.startYear ?? ''}
                onChange={(ev) => {
                  const arr = [...form.experiences];
                  const v = ev.target.value;
                  arr[i] = { ...x, startYear: v ? Number(v) : null };
                  update('experiences', arr);
                }}
              />
              <TextField
                size="small"
                label="To"
                type="number"
                value={x.endYear ?? ''}
                onChange={(ev) => {
                  const arr = [...form.experiences];
                  const v = ev.target.value;
                  arr[i] = { ...x, endYear: v ? Number(v) : null };
                  update('experiences', arr);
                }}
              />
              <IconButton
                size="small"
                aria-label="Remove experience"
                onClick={() =>
                  update(
                    'experiences',
                    form.experiences.filter((_, j) => j !== i)
                  )
                }
              >
                <DeleteOutlineIcon fontSize="small" />
              </IconButton>
            </Box>
          ))}
          {form.experiences.length === 0 && (
            <Typography variant="caption" color="text.secondary">
              No experiences added yet — [Experience N] placeholders will be stripped from the
              composed resume until you add entries.
            </Typography>
          )}
        </Stack>
      </Paper>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1 }}>
          <Typography variant="subtitle1">Education</Typography>
          <Button
            size="small"
            startIcon={<AddIcon />}
            onClick={() => update('education', [...form.education, blankEdu()])}
          >
            Add
          </Button>
        </Stack>
        <Stack spacing={2}>
          {form.education.map((e, i) => (
            <Box
              key={i}
              sx={{
                display: 'grid',
                gridTemplateColumns: '1fr 1fr 1fr 80px 80px 36px',
                gap: 1,
                alignItems: 'center',
              }}
            >
              <TextField
                size="small"
                label="Degree"
                value={e.degree}
                onChange={(ev) => {
                  const arr = [...form.education];
                  arr[i] = { ...e, degree: ev.target.value };
                  update('education', arr);
                }}
              />
              <TextField
                size="small"
                label="School"
                value={e.school}
                onChange={(ev) => {
                  const arr = [...form.education];
                  arr[i] = { ...e, school: ev.target.value };
                  update('education', arr);
                }}
              />
              <TextField
                size="small"
                label="Location"
                value={e.location}
                onChange={(ev) => {
                  const arr = [...form.education];
                  arr[i] = { ...e, location: ev.target.value };
                  update('education', arr);
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
                  update('education', arr);
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
                  update('education', arr);
                }}
              />
              <IconButton
                size="small"
                aria-label="Remove education"
                onClick={() =>
                  update(
                    'education',
                    form.education.filter((_, j) => j !== i)
                  )
                }
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
            onClick={() => update('certifications', [...form.certifications, blankCert()])}
          >
            Add
          </Button>
        </Stack>
        <Stack spacing={2}>
          {form.certifications.map((c, i) => (
            <Box
              key={i}
              sx={{
                display: 'grid',
                gridTemplateColumns: '1fr 1fr 80px 36px',
                gap: 1,
                alignItems: 'center',
              }}
            >
              <TextField
                size="small"
                label="Name"
                value={c.name}
                onChange={(ev) => {
                  const arr = [...form.certifications];
                  arr[i] = { ...c, name: ev.target.value };
                  update('certifications', arr);
                }}
              />
              <TextField
                size="small"
                label="Issuer"
                value={c.issuer}
                onChange={(ev) => {
                  const arr = [...form.certifications];
                  arr[i] = { ...c, issuer: ev.target.value };
                  update('certifications', arr);
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
                  update('certifications', arr);
                }}
              />
              <IconButton
                size="small"
                aria-label="Remove certification"
                onClick={() =>
                  update(
                    'certifications',
                    form.certifications.filter((_, j) => j !== i)
                  )
                }
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
          disabled={mut.isPending}
          onClick={() =>
            mut.mutate({
              displayName: form.displayName,
              headline: form.headline,
              location: form.location,
              phone: form.phone,
              personalEmail: form.personalEmail,
              linkedinUrl: form.linkedinUrl,
              education: form.education,
              certifications: form.certifications,
              experiences: form.experiences,
            })
          }
        >
          {mut.isPending ? 'Saving…' : 'Save group profile'}
        </Button>
        {mut.isError && (
          <Alert severity="error" sx={{ mt: 1 }}>
            Could not save group profile.
          </Alert>
        )}
        {mut.isSuccess && !mut.isPending && (
          <Typography variant="caption" color="success.main" sx={{ ml: 2 }}>
            Saved.
          </Typography>
        )}
      </Box>
    </Stack>
  );
}
