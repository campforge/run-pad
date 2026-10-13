#!/usr/bin/perl
# Regenerates installer/windows/License.rtf (the MSI license dialog) from installer/EULA.txt.
# Run from the repo root after editing the agreement, and commit both files:
#   perl scripts/make-license-rtf.pl < installer/EULA.txt > installer/windows/License.rtf
# Paragraphs are separated by blank lines; wrapped lines are joined; "N. " lines become bold
# headings; "  - " lines become bullets; lines indented by 4 spaces become indented paragraphs.
use strict;
use warnings;

local $/;
my $text = <STDIN>;
$text =~ s/\r//g;

my @paras;
my $cur = '';
for my $line (split /\n/, $text) {
    if ($line =~ /^\s*$/) {
        push @paras, $cur if length $cur;
        $cur = '';
        next;
    }
    my $starts_item = $line =~ /^  - /;
    my $starts_indent = $line =~ /^    \S/ && $cur !~ /^  - /;
    if ($starts_item || $starts_indent) {
        push @paras, $cur if length $cur;
        $cur = $line;
        next;
    }
    if (length $cur) {
        $line =~ s/^\s+//;
        $cur .= " $line";
    } else {
        $cur = $line;
    }
}
push @paras, $cur if length $cur;

my $nl = "\r\n";
my $out = '{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss\fcharset0 Segoe UI;}}' . $nl
        . '\viewkind4\uc1\pard\f0\fs18' . $nl;
my $first = 1;
for my $p (@paras) {
    (my $e = $p) =~ s/([\\{}])/\\$1/g;
    if ($first) {
        $out .= '\pard\sa200{\b\fs24 ' . $e . '}\par' . $nl;
        $first = 0;
    } elsif ($e =~ /^\d+\. /) {
        $out .= '\pard\sb240\sa100{\b ' . $e . '}\par' . $nl;
    } elsif ($e =~ s/^  - //) {
        $out .= '\pard\fi-220\li440\tx440\sa60\bullet\tab ' . $e . '\par' . $nl;
    } elsif ($e =~ s/^    //) {
        $out .= '\pard\li440\sa120 ' . $e . '\par' . $nl;
    } else {
        $out .= '\pard\sa120 ' . $e . '\par' . $nl;
    }
}
$out .= '}' . $nl;
binmode STDOUT;
print $out;
