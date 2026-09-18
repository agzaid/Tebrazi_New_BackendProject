namespace Tebrazi.SharedKernel.Enums;

// Ported one-for-one from server/prisma/schema.prisma. Member names and ordering match the
// Prisma enums exactly. These are persisted and serialized AS STRINGS, never as ints — the
// React client compares against the literal values ("PHYSICIAN", "ACCEPTED", ...).

public enum UserRole { ADMIN, MANAGER, USER, VIEWER }

public enum UserType { PHYSICIAN, PATIENT, RECEPTIONIST, STAFF }

public enum OrgRole { OWNER, ADMIN, MEMBER, VIEWER }

public enum SubscriptionPlan { FREE, STARTER, PRO, ENTERPRISE }

public enum SubscriptionStatus { ACTIVE, PAST_DUE, CANCELED, TRIALING }

public enum Gender { MALE, FEMALE, OTHER }

public enum DocumentType { PDF, IMAGE, VIDEO, AUDIO, SPREADSHEET, DOCUMENT, OTHER }
