const ORDER = {
  HR: 0,
  ASSESSMENT: 1,
  TECH_1: 2,
  TECH_2: 3,
  TECH_3: 4,
  CLIENT: 5,
  OFFER: 6,
};

export function techIndex(type) {
  if (type === 'TECH_1') return 1;
  if (type === 'TECH_2') return 2;
  if (type === 'TECH_3') return 3;
  return null;
}

/** Whether this type can be created as the next step after parentType */
export function canFollow(parentType, nextType) {
  if (nextType === 'CLIENT' || nextType === 'OFFER') return true;
  if (parentType === 'HR' && nextType === 'ASSESSMENT') return true;
  if (parentType === 'ASSESSMENT' && nextType === 'TECH_1') return true;
  if (parentType === 'HR' && nextType === 'TECH_1') return true;
  if (parentType === 'TECH_1' && nextType === 'TECH_2') return true;
  if (parentType === 'TECH_2' && nextType === 'TECH_3') return true;
  if (parentType === 'TECH_3' && (nextType === 'CLIENT' || nextType === 'OFFER')) return true;
  return false;
}

export function allowedNextTypes(parentType) {
  if (!parentType) return ['HR'];
  const out = [];
  if (parentType === 'HR') out.push('ASSESSMENT', 'TECH_1', 'CLIENT', 'OFFER');
  if (parentType === 'ASSESSMENT') out.push('TECH_1', 'CLIENT', 'OFFER');
  if (parentType === 'TECH_1') out.push('TECH_2', 'CLIENT', 'OFFER');
  if (parentType === 'TECH_2') out.push('TECH_3', 'CLIENT', 'OFFER');
  if (parentType === 'TECH_3') out.push('CLIENT', 'OFFER');
  return [...new Set(out)];
}

export { ORDER };
