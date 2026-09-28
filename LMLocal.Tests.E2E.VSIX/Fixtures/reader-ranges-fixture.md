# Reader Ranges Fixture — ground truth for range/offset scenarios

This fixture is intentionally repetitive so that range requests cannot be
answered from memory of adjacent text. Every section contains unique anchor
lines that let a test verify EXACT retrieval boundaries.

> DefaultAnchor-Block

## 1. First Section

RANGE-ANCHOR-A1-OPEN

This is the body of section 1. The next section marker is far below.

RANGE-ANCHOR-A1-CLOSE

## 2. Second Section

RANGE-ANCHOR-A2-OPEN

This is the body of section 2. The lines here are not unique on purpose,
but the anchors above and below each stretch are what the tests verify.

RANGE-ANCHOR-A2-CLOSE

## 3. Third Section

RANGE-ANCHOR-A3-OPEN

This is the body of section 3.

RANGE-ANCHOR-A3-CLOSE

## Appendix A. References

RANGE-ANCHOR-APP-OPEN

Appendix references. The end of the file is a unique tail line.

RANGE-ANCHOR-APP-CLOSE

## Tail section

RANGE-ANCHOR-TAIL-OPEN

> Reader-Ranges-Tail-Line is the last line of this fixture.

Tail-Anchor-Open

# First Section (copy for recovery)

RANGE-ANCHOR-A1-OPEN-COPY

Recovery section with a copy of the first-section anchor.

RANGE-ANCHOR-A1-CLOSE-COPY

# End of fixture