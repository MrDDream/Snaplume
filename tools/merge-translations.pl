#!/usr/bin/perl
# Adds the keys of a fragment {"fr": {"Key": "text", ...}, "en": {...}, ...} at the end of the six
# src/Snaplume.Core/Languages/*.json files, keeping their order, CRLF line endings and UTF-8 text.
# Usage: perl tools/merge-translations.pl fragment.json [more.json ...]
# Every language must have the same keys; a key already present is an error (nothing is written).
use strict;
use warnings;
use JSON::PP;

my $dir = 'src/Snaplume.Core/Languages';
my @languages = qw(fr en de es it ar);
my %add = map { $_ => [] } @languages;
my %seen;

for my $fragment (@ARGV) {
    local $/;
    open my $f, '<:raw', $fragment or die "$fragment: $!";
    my $raw = <$f>;
    close $f;
    my $data = JSON::PP->new->utf8->decode($raw);
    # Keys in the order of the "fr" section (a key is a string followed by a colon).
    my @reference = $raw =~ /"fr"\s*:\s*\{(.*?)\}/s ? ($1 =~ /"((?:[^"\\]|\\.)+)"\s*:/g) : ();
    for my $lang (@languages) {
        my $values = $data->{$lang} or die "$fragment: no \"$lang\" section\n";
        die "$fragment: \"$lang\" has not the same keys as \"fr\"\n"
            unless join("\n", sort keys %$values) eq join("\n", sort @reference);
        for my $key (@reference) {
            die "$fragment: $key added twice\n" if $lang eq 'fr' && $seen{$key}++;
            push @{ $add{$lang} }, [$key, $values->{$key}];
        }
    }
}

my $encoder = JSON::PP->new->allow_nonref;
for my $lang (@languages) {
    my $file = "$dir/$lang.json";
    local $/;
    open my $in, '<:raw', $file or die "$file: $!";
    my $text = <$in>;
    close $in;
    my $existing = JSON::PP->new->utf8->decode($text);
    my $lines = '';
    for my $pair (@{ $add{$lang} }) {
        my ($key, $value) = @$pair;
        die "$file: $key already exists\n" if exists $existing->{$key};
        my $json = $encoder->encode($value);
        utf8::encode($json);
        $lines .= ",\r\n  \"$key\": $json";
    }
    $text =~ s/\r?\n\}\s*\z// or die "$file: unexpected end\n";
    $text .= $lines . "\r\n}\n";
    open my $out, '>:raw', $file or die "$file: $!";
    print $out $text;
    close $out;
    print "$lang: +", scalar(@{ $add{$lang} }), "\n";
}
